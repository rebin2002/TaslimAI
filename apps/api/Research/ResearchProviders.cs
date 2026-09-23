using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Research;

public sealed record ResearchSearchRequest(ResearchGenerationInput Input, ResearchPlan Plan, ResearchSourcePreference Preferences);
public sealed record ResearchSearchResult(IReadOnlyList<ResearchSourceCandidate> Sources, IReadOnlyList<ResearchEvidenceCandidate> Evidence, AiUsageMetadata Usage);
public sealed record ResearchReportPrompt(ResearchGenerationInput Input, ResearchPlan Plan, IReadOnlyList<ResearchSourceCandidate> Sources, IReadOnlyList<ResearchEvidenceCandidate> Evidence, ResearchProjectContext? Project);
public sealed record ResearchReportProviderResult(ResearchDraft Draft, AiUsageMetadata Usage);
public sealed record ResearchProviderErrorDetails(int HttpStatusCode, string? ErrorType, string? ErrorCode, string? ErrorParam, string FailureCategory);

public interface IResearchPlanner
{
    Task<ResearchPlan> PlanAsync(ResearchGenerationInput input, ResearchProjectContext? project, CancellationToken cancellationToken = default);
}

public interface IResearchSearchProvider
{
    Task<ResearchSearchResult> SearchAsync(ResearchSearchRequest request, ResearchGenerationOptions options, CancellationToken cancellationToken = default);
}

public interface IResearchContentFetcher
{
    Task<IReadOnlyList<ResearchSourceCandidate>> FetchAsync(IReadOnlyList<ResearchSourceCandidate> sources, ResearchGenerationOptions options, CancellationToken cancellationToken = default);
}

public interface IResearchEvidenceProcessor
{
    IReadOnlyList<ResearchEvidenceCandidate> Normalize(IReadOnlyList<ResearchSourceCandidate> sources, IReadOnlyList<ResearchEvidenceCandidate> providerEvidence, ResearchGenerationOptions options);
}

public interface IResearchReportProvider
{
    Task<ResearchReportProviderResult> GenerateAsync(ResearchReportPrompt prompt, ResearchGenerationOptions options, CancellationToken cancellationToken = default);
}

public interface IResearchCitationValidator
{
    void Validate(ResearchDraft draft, IReadOnlySet<string> sourceCitationIds, ResearchGenerationOptions options);
}

public sealed class DeterministicResearchPlanner : IResearchPlanner
{
    public Task<ResearchPlan> PlanAsync(ResearchGenerationInput input, ResearchProjectContext? project, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var focus = string.IsNullOrWhiteSpace(input.GeographicFocus) ? string.Empty : $" Geographic focus: {input.GeographicFocus}.";
        var period = string.IsNullOrWhiteSpace(input.TimePeriod) ? string.Empty : $" Time period: {input.TimePeriod}.";
        var objective = $"Produce a cited {input.ReportType.Replace('_', ' ')} for the requested audience without unsupported claims.{focus}{period}";
        var queries = new List<string> { input.Question };
        if (!string.IsNullOrWhiteSpace(input.GeographicFocus)) queries.Add($"{input.Question} {input.GeographicFocus}");
        if (!string.IsNullOrWhiteSpace(input.TimePeriod)) queries.Add($"{input.Question} {input.TimePeriod}");
        queries.Add($"{input.Question} official statistics report data");
        queries.Add($"{input.Question} competitors distribution channels risks");
        queries.Add($"{input.Question} recent developments evidence");
        var bounded = queries.Distinct(StringComparer.OrdinalIgnoreCase).Take(ResearchGenerationDefaults.MaxQueries(input.Depth)).ToArray();
        return Task.FromResult(new ResearchPlan(input.Question, objective, bounded, [input.ReportType, "market context", "evidence and risks"], input.GeographicFocus, input.TimePeriod, ["official", "industry", "news"]));
    }
}

public sealed class PassthroughResearchContentFetcher : IResearchContentFetcher
{
    public Task<IReadOnlyList<ResearchSourceCandidate>> FetchAsync(IReadOnlyList<ResearchSourceCandidate> sources, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(sources);
    }
}

public sealed class DeterministicResearchEvidenceProcessor : IResearchEvidenceProcessor
{
    public IReadOnlyList<ResearchEvidenceCandidate> Normalize(IReadOnlyList<ResearchSourceCandidate> sources, IReadOnlyList<ResearchEvidenceCandidate> providerEvidence, ResearchGenerationOptions options)
    {
        var valid = sources.Select(source => source.CitationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<ResearchEvidenceCandidate>();
        foreach (var item in providerEvidence)
        {
            if (!valid.Contains(item.CitationId) || string.IsNullOrWhiteSpace(item.Excerpt)) continue;
            evidence.Add(item with
            {
                Excerpt = Trim(item.Excerpt, options.MaxEvidenceCharacters),
                Context = string.IsNullOrWhiteSpace(item.Context) ? null : Trim(item.Context, options.MaxEvidenceCharacters),
            });
            if (evidence.Count >= options.MaxTotalEvidenceCharacters / Math.Max(1, options.MaxEvidenceCharacters)) break;
        }
        foreach (var source in sources)
        {
            if (evidence.Any(item => item.CitationId.Equals(source.CitationId, StringComparison.OrdinalIgnoreCase))) continue;
            if (string.IsNullOrWhiteSpace(source.ExtractedText) && string.IsNullOrWhiteSpace(source.Snippet)) continue;
            evidence.Add(new ResearchEvidenceCandidate(source.CitationId, "source context", Trim(source.ExtractedText ?? source.Snippet!, options.MaxEvidenceCharacters), null, source.PublishedAt));
        }
        return evidence.Take(options.MaxTotalEvidenceCharacters / Math.Max(1, options.MaxEvidenceCharacters)).ToArray();
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..Math.Max(1, max - 3)].TrimEnd() + "...";
}

public sealed class OpenAiResearchSearchProvider(
    HttpClient httpClient,
    IOptions<AiOptions> aiOptions,
    IOptions<ResearchGenerationOptions> researchOptions,
    ILogger<OpenAiResearchSearchProvider> logger) : IResearchSearchProvider
{
    private readonly AiOptions ai = aiOptions.Value;
    private readonly ResearchGenerationOptions settings = researchOptions.Value;

    public async Task<ResearchSearchResult> SearchAsync(ResearchSearchRequest request, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        if (!ai.OpenAI.Enabled || string.IsNullOrWhiteSpace(ai.OpenAI.ApiKey)) throw new ResearchSearchUnavailableException();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ProviderTimeoutSeconds, 10, 600)));
        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(options.SearchModel) ? settings.SearchModel : options.SearchModel,
            ["tools"] = new object[] { BuildWebSearchTool(request) },
            ["tool_choice"] = "required",
            ["include"] = new[] { "web_search_call.action.sources" },
            ["input"] = BuildInput(request),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildResponsesUrl(ai.OpenAI.BaseUrl));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ai.OpenAI.ApiKey);
        message.Content = JsonContent.Create(payload);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try { response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ResearchSearchTimeoutException(); }
        catch (HttpRequestException exception) { logger.LogWarning(exception, "Research search provider request failed safely. FailureCategory={FailureCategory}", "transient"); throw new ResearchSearchFailedException(); }
        using (response.Content)
        {
            var httpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var details = await ReadProviderErrorAsync(response.Content, httpStatus, timeout.Token);
                logger.LogWarning(
                    "Research search provider rejected request. HttpStatus={HttpStatus}; ErrorType={ErrorType}; ErrorCode={ErrorCode}; ErrorParam={ErrorParam}; FailureCategory={FailureCategory}",
                    details.HttpStatusCode,
                    details.ErrorType ?? "none",
                    details.ErrorCode ?? "none",
                    details.ErrorParam ?? "none",
                    details.FailureCategory);
                throw httpStatus is 401 or 403
                    ? new ResearchSearchUnavailableException(details)
                    : new ResearchSearchFailedException(details);
            }
            try
            {
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                return Parse(document.RootElement, stopwatch.ElapsedMilliseconds, options);
            }
            catch (ResearchSearchFailedException)
            {
                throw;
            }
            catch (JsonException)
            {
                logger.LogWarning("Research search provider returned malformed output. HttpStatus={HttpStatus}; FailureCategory={FailureCategory}", httpStatus, "malformed_response");
                throw new ResearchSearchFailedException(new ResearchProviderErrorDetails(httpStatus, null, null, null, "malformed_response"));
            }
            catch (InvalidOperationException)
            {
                logger.LogWarning("Research search provider returned malformed output. HttpStatus={HttpStatus}; FailureCategory={FailureCategory}", httpStatus, "malformed_response");
                throw new ResearchSearchFailedException(new ResearchProviderErrorDetails(httpStatus, null, null, null, "malformed_response"));
            }
        }
    }

    private static Dictionary<string, object?> BuildWebSearchTool(ResearchSearchRequest request)
    {
        var tool = new Dictionary<string, object?>
        {
            ["type"] = "web_search",
            ["search_context_size"] = request.Input.Depth.Equals("deep", StringComparison.OrdinalIgnoreCase) ? "high" : request.Input.Depth.Equals("quick", StringComparison.OrdinalIgnoreCase) ? "low" : "medium",
            ["external_web_access"] = true,
        };
        var filters = BuildFilters(request.Preferences);
        if (filters is not null) tool["filters"] = filters;
        return tool;
    }

    private static Dictionary<string, object?>? BuildFilters(ResearchSourcePreference preferences)
    {
        var filters = new Dictionary<string, object?>();
        if (preferences.PreferredDomains.Count > 0) filters["allowed_domains"] = preferences.PreferredDomains;
        if (preferences.ExcludedDomains.Count > 0) filters["blocked_domains"] = preferences.ExcludedDomains;
        return filters.Count == 0 ? null : filters;
    }

    private static async Task<ResearchProviderErrorDetails> ReadProviderErrorAsync(HttpContent content, int status, CancellationToken cancellationToken)
    {
        string? errorType = null;
        string? errorCode = null;
        string? errorParam = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(await content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                errorType = ReadSafeProviderField(error, "type");
                errorCode = ReadSafeProviderField(error, "code");
                errorParam = ReadSafeProviderField(error, "param");
            }
        }
        catch (JsonException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

        var category = status switch
        {
            401 or 403 => "configuration",
            429 => "rate_limited",
            >= 500 => "transient",
            >= 400 => "provider",
            _ => "unknown",
        };
        return new ResearchProviderErrorDetails(status, errorType, errorCode, errorParam, category);
    }

    private static string? ReadSafeProviderField(JsonElement error, string property)
    {
        if (!error.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text.Length <= 160 ? text : text[..160];
    }

    private static string BuildInput(ResearchSearchRequest request) =>
        $"Research question: {request.Input.Question}\nReport type: {request.Input.ReportType}\nDepth: {request.Input.Depth}\n{(string.IsNullOrWhiteSpace(request.Input.GeographicFocus) ? string.Empty : $"Geographic focus: {request.Input.GeographicFocus}\n")}{(string.IsNullOrWhiteSpace(request.Input.TimePeriod) ? string.Empty : $"Time period: {request.Input.TimePeriod}\n")}Search queries:\n- {string.Join("\n- ", request.Plan.SearchQueries)}\n\nReturn a concise evidence brief, not a final report. State sourced facts and uncertainty. Cite every factual paragraph with the web-search citations. Do not invent URLs, sources, numbers, quotations, or metadata.";

    private static ResearchSearchResult Parse(JsonElement root, long latencyMs, ResearchGenerationOptions options)
    {
        var output = root.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.Array ? outputElement.EnumerateArray().ToArray() : [];
        var annotations = new List<(string Url, string? Title, int Start, int End)>();
        var answerParts = new List<string>();
        var sourceUrls = new List<string>();
        foreach (var item in output)
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "web_search_call" && item.TryGetProperty("action", out var action) && action.TryGetProperty("sources", out var sourceList) && sourceList.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sourceList.EnumerateArray())
                {
                    if (source.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String) sourceUrls.Add(url.GetString()!);
                }
            }
            if (item.TryGetProperty("type", out type) && type.GetString() == "message" && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (!part.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String) continue;
                    var text = textElement.GetString() ?? string.Empty;
                    answerParts.Add(text);
                    if (!part.TryGetProperty("annotations", out var annotationList) || annotationList.ValueKind != JsonValueKind.Array) continue;
                    foreach (var annotation in annotationList.EnumerateArray())
                    {
                        var citation = annotation.TryGetProperty("url_citation", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : annotation;
                        if (!citation.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) continue;
                        annotations.Add((url.GetString()!, citation.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null, citation.TryGetProperty("start_index", out var start) && start.TryGetInt32(out var startValue) ? startValue : 0, citation.TryGetProperty("end_index", out var end) && end.TryGetInt32(out var endValue) ? endValue : text.Length));
                    }
                }
            }
        }
        var answer = string.Join("\n\n", answerParts);
        var orderedUrls = sourceUrls.Concat(annotations.Select(item => item.Url)).Where(IsHttpUrl).Distinct(StringComparer.OrdinalIgnoreCase).Take(options.MaxSourceCount).ToArray();
        if (orderedUrls.Length == 0) throw new ResearchSearchFailedException();
        var sourceByUrl = new Dictionary<string, ResearchSourceCandidate>(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<ResearchEvidenceCandidate>();
        for (var index = 0; index < orderedUrls.Length; index++)
        {
            var url = orderedUrls[index];
            var annotation = annotations.FirstOrDefault(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            var title = string.IsNullOrWhiteSpace(annotation.Title) ? new Uri(url).Host : annotation.Title!;
            var citationId = $"S{index + 1}";
            var canonical = Canonicalize(url);
            var citationMetadata = annotation.Url is null ? null : JsonSerializer.Serialize(new { citationStartIndex = annotation.Start, citationEndIndex = annotation.End });
            sourceByUrl[url] = new ResearchSourceCandidate(citationId, url, canonical, Trim(title, options.MaxSourceTitleCharacters), new Uri(url).Host, null, null, DateTime.UtcNow, "web", Trim(answer, options.MaxSourceSnippetCharacters), Trim(answer, options.MaxSourceTextCharacters), null, index + 1, true, citationMetadata);
            if (annotations.Any(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) evidence.Add(new ResearchEvidenceCandidate(citationId, "web evidence", Trim(answer, options.MaxEvidenceCharacters), null, null));
        }
        var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String ? modelElement.GetString()! : options.SearchModel;
        var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
        var inputTokens = ReadInt(usage, "input_tokens");
        var outputTokens = ReadInt(usage, "output_tokens");
        var metadata = JsonSerializer.Serialize(new { researchStage = "search", searchSourceCount = sourceByUrl.Count, providerCost = "not_returned_by_search_tool" });
        return new ResearchSearchResult(sourceByUrl.Values.ToArray(), evidence, new AiUsageMetadata("openai", model, inputTokens, null, outputTokens, null, null, (int)Math.Min(int.MaxValue, latencyMs), "completed", false, PricingVersion: options.PricingVersion, Currency: "USD", CostBasis: UsageCostBasis.Estimated, SafeMetadataJson: metadata));
    }

    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    private static string Canonicalize(string url) { var uri = new Uri(url); var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty }; return builder.Uri.ToString().TrimEnd('/'); }
    private static string Trim(string value, int max) => value.Length <= max ? value : value[..Math.Max(1, max - 3)].TrimEnd() + "...";
    private static int? ReadInt(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static Uri BuildResponsesUrl(string baseUrl) => new Uri($"{(string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/'))}/responses", UriKind.Absolute);
}

public sealed class AiResearchReportProvider(IChatCompletionService completion, IResearchPromptBuilder promptBuilder) : IResearchReportProvider
{
    public async Task<ResearchReportProviderResult> GenerateAsync(ResearchReportPrompt prompt, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        var request = new AiChatRequest([new AiChatMessage("user", promptBuilder.Build(prompt, options))], "You are Taslim Research Studio. Return only the required JSON research draft.", options.ReportTier, MaxOutputTokens: options.MaxOutputTokens, StructuredOutput: ResearchStructuredOutput.Spec);
        var result = await completion.CompleteAsync(request, cancellationToken);
        ResearchDraft draft;
        try { draft = JsonSerializer.Deserialize<ResearchDraft>(result.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ResearchOutputValidationException(); }
        catch (JsonException exception) { throw new ResearchOutputInvalidException(exception); }
        return new ResearchReportProviderResult(draft, result.Usage with { SafeMetadataJson = JsonSerializer.Serialize(new { researchStage = "report" }) });
    }
}

public interface IResearchPromptBuilder
{
    string Build(ResearchReportPrompt prompt, ResearchGenerationOptions options);
}

public sealed class ResearchPromptBuilder : IResearchPromptBuilder
{
    public string Build(ResearchReportPrompt prompt, ResearchGenerationOptions options)
    {
        var context = new StringBuilder();
        foreach (var source in prompt.Sources)
        {
            context.AppendLine($"[{source.CitationId}] {source.Title} | {source.Domain} | {source.Url ?? "user-provided source"}");
            var sourceEvidence = prompt.Evidence.Where(item => item.CitationId.Equals(source.CitationId, StringComparison.OrdinalIgnoreCase));
            foreach (var evidence in sourceEvidence) context.AppendLine($"Evidence: {evidence.Excerpt}");
        }
        if (context.Length > options.MaxContextCharacters) throw new ResearchContextLimitException();
        var project = prompt.Project is null ? string.Empty : $"\nProject context (bounded and authorized): {prompt.Project.Name}\n{prompt.Project.Instructions}\n{prompt.Project.ContextNotes}\n";
        return $"Research question: {prompt.Input.Question}\nObjective: {prompt.Plan.Objective}\nReport type: {prompt.Input.ReportType}\nRequested language: {prompt.Input.Language}\nAudience: {prompt.Input.Audience ?? "general reader"}{project}\n\nEvidence context follows. Use only this context for factual claims. Every factual block must include one or more citation IDs from the supplied source IDs. Do not create URLs or citations yourself. Distinguish sourced facts from analysis and state uncertainty. Never invent numbers, dates, quotations, companies, studies, statistics, or sources.\n\n{context}\n\nReturn a structured report with an executive summary, key findings, sections, conclusion, and the source citation IDs used.";
    }
}

public static class ResearchStructuredOutput
{
    public static readonly AiStructuredOutputSpec Spec = new("research_draft", JsonDocument.Parse("""
    {"type":"object","additionalProperties":false,"properties":{"title":{"type":"string","maxLength":255},"subtitle":{"type":["string","null"],"maxLength":400},"language":{"type":"string","enum":["en","ar","ku","auto"]},"executiveSummary":{"type":"string","maxLength":8000},"keyFindings":{"type":"array","maxItems":12,"items":{"$ref":"#/$defs/block"}},"sections":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object","additionalProperties":false,"properties":{"heading":{"type":"string","maxLength":240},"blocks":{"type":"array","maxItems":120,"items":{"$ref":"#/$defs/block"}}},"required":["heading","blocks"]}},"conclusion":{"type":"string","maxLength":8000},"sources":{"type":"array","maxItems":32,"items":{"type":"string","maxLength":16}}},"required":["title","subtitle","language","executiveSummary","keyFindings","sections","conclusion","sources"],"$defs":{"block":{"type":"object","additionalProperties":false,"properties":{"type":{"type":"string","enum":["paragraph","bullets","numbered_list","table","key_finding"]},"text":{"type":["string","null"],"maxLength":8000},"items":{"type":["array","null"],"maxItems":40,"items":{"type":"string","maxLength":8000}},"rows":{"type":["array","null"],"maxItems":100,"items":{"type":"object","additionalProperties":false,"properties":{"cells":{"type":"array","maxItems":12,"items":{"type":"string","maxLength":8000}}},"required":["cells"]}},"citationIds":{"type":"array","maxItems":8,"items":{"type":"string","maxLength":16}}},"required":["type","text","items","rows","citationIds"]}}}
    """).RootElement.Clone());
}

public sealed class ResearchSearchUnavailableException(ResearchProviderErrorDetails? details = null) : Exception("The research search provider is unavailable.")
{
    public ResearchProviderErrorDetails? Details { get; } = details;
}

public sealed class ResearchSearchFailedException(ResearchProviderErrorDetails? details = null) : Exception("The research search provider failed.")
{
    public ResearchProviderErrorDetails? Details { get; } = details;
}

public sealed class ResearchSearchTimeoutException : Exception;
public sealed class ResearchOutputInvalidException(Exception inner) : Exception("The research report output was invalid.", inner);
