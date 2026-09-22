using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Taslim.Api.Ai;

public sealed class OpenAiProvider(
    HttpClient httpClient,
    IOptions<AiOptions> options,
    ILogger<OpenAiProvider> logger) : IAiProvider
{
    private readonly AiOptions settings = options.Value;

    public string Key => "openai";

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(
        AiChatRequest request,
        AiProviderSelection selection,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!settings.OpenAI.Enabled || string.IsNullOrWhiteSpace(settings.OpenAI.ApiKey))
            throw new AiProviderUnavailableException();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 5, 300)));
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildResponsesUrl(settings.OpenAI.BaseUrl));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAI.ApiKey);
        var lastMessageIndex = request.Messages.Count - 1;
        var structuredOutputRequested = request.StructuredOutput is not null || request.JsonMode;
        var requestPayload = new Dictionary<string, object?>
        {
            ["model"] = selection.ModelKey,
            ["instructions"] = request.SystemInstruction,
            ["input"] = request.Messages.Select((item, index) => new
            {
                role = item.Role,
                content = index == lastMessageIndex && request.Attachments?.Count > 0
                    ? BuildMultimodalContent(item.Content, request.Attachments)
                    : (object)item.Content,
            }).ToArray(),
            ["stream"] = request.EnableStreaming,
        };
        if (request.MaxOutputTokens is { } maxOutputTokens) requestPayload["max_output_tokens"] = maxOutputTokens;
        if (request.StructuredOutput is { } structured)
        {
            var format = new Dictionary<string, object?>
            {
                ["type"] = "json_schema",
                ["name"] = structured.Name,
                ["strict"] = structured.Strict,
                ["schema"] = structured.Schema,
            };
            if (!string.IsNullOrWhiteSpace(structured.Description)) format["description"] = structured.Description;
            requestPayload["text"] = new Dictionary<string, object?> { ["format"] = format };
        }
        else if (request.JsonMode)
        {
            requestPayload["text"] = new { format = new { type = "json_object" } };
        }
        message.Content = JsonContent.Create(requestPayload);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, request.EnableStreaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI provider timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}", selection.ProviderKey, selection.ModelKey, structuredOutputRequested, request.EnableStreaming);
            throw new AiProviderTimeoutException();
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "OpenAI provider request failed. FailureCategory={FailureCategory}; ProviderKey={ProviderKey}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}", AiProviderFailureCategories.Transient, selection.ProviderKey, selection.ModelKey, structuredOutputRequested, request.EnableStreaming);
            throw new AiProviderException(failureCategory: AiProviderFailureCategories.Transient, modelKey: selection.ModelKey, structuredOutputRequested: structuredOutputRequested, streamingRequested: request.EnableStreaming);
        }

        using (response.Content)
        {
            if (!response.IsSuccessStatusCode)
            {
                string? providerError;
                try
                {
                    providerError = await ReadSafeProviderErrorAsync(response.Content, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AiProviderTimeoutException();
                }
                var category = ClassifyFailure((int)response.StatusCode);
                logger.LogWarning(
                    "OpenAI provider request rejected. FailureCategory={FailureCategory}; HttpStatus={HttpStatus}; ProviderErrorCode={ProviderErrorCode}; ProviderKey={ProviderKey}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}",
                    category,
                    (int)response.StatusCode,
                    providerError,
                    selection.ProviderKey,
                    selection.ModelKey,
                    structuredOutputRequested,
                    request.EnableStreaming);
                throw new AiProviderException((int)response.StatusCode, providerError, category, selection.ModelKey, structuredOutputRequested, request.EnableStreaming);
            }

            if (!request.EnableStreaming)
            {
                string body;
                try
                {
                    body = await response.Content.ReadAsStringAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AiProviderTimeoutException();
                }

                using var json = ParseProviderJson(body, selection, structuredOutputRequested, request.EnableStreaming);
                var content = ReadOutputText(json.RootElement);
                if (content is null)
                    throw new AiProviderException(failureCategory: AiProviderFailureCategories.MalformedResponse, modelKey: selection.ModelKey, structuredOutputRequested: structuredOutputRequested, streamingRequested: request.EnableStreaming);
                yield return new AiMessageDelta(content);
                yield return new AiMessageCompleted(ReadUsage(json.RootElement, selection, stopwatch.ElapsedMilliseconds));
                yield break;
            }

            await using var stream = await OpenResponseStreamAsync(response.Content, timeout.Token, cancellationToken, selection, structuredOutputRequested);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var data = new StringBuilder();
            AiUsageMetadata? usage = null;
            while (!reader.EndOfStream)
            {
                var line = await ReadLineAsync(reader, timeout.Token, cancellationToken, selection, structuredOutputRequested);
                if (line is null) break;
                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    data.AppendLine(line[5..].Trim());
                    continue;
                }
                if (line.Length != 0 || data.Length == 0) continue;

                var payload = data.ToString().Trim();
                data.Clear();
                if (payload.Length == 0 || payload == "[DONE]") continue;
                using var json = ParseProviderJson(payload, selection, structuredOutputRequested, request.EnableStreaming);
                var root = json.RootElement;
                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (type == "response.output_text.delta" && root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                {
                    yield return new AiMessageDelta(delta.GetString() ?? string.Empty);
                }
                else if (type == "response.completed")
                {
                    usage = ReadUsage(root, selection, stopwatch.ElapsedMilliseconds);
                }
                else if (type is "error" or "response.failed")
                {
                    var providerError = ReadSafeProviderError(root);
                    logger.LogWarning("OpenAI provider emitted a failure event. FailureCategory={FailureCategory}; ProviderErrorCode={ProviderErrorCode}; ProviderKey={ProviderKey}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming={Streaming}", AiProviderFailureCategories.Transient, providerError, selection.ProviderKey, selection.ModelKey, structuredOutputRequested, request.EnableStreaming);
                    throw new AiProviderException(providerErrorCode: providerError, failureCategory: AiProviderFailureCategories.Transient, modelKey: selection.ModelKey, structuredOutputRequested: structuredOutputRequested, streamingRequested: request.EnableStreaming);
                }
            }

            if (usage is null)
                throw new AiProviderException(failureCategory: AiProviderFailureCategories.MalformedResponse, modelKey: selection.ModelKey, structuredOutputRequested: structuredOutputRequested, streamingRequested: request.EnableStreaming);
            yield return new AiMessageCompleted(usage);
        }
    }

    private async Task<Stream> OpenResponseStreamAsync(HttpContent content, CancellationToken timeoutToken, CancellationToken requestToken, AiProviderSelection selection, bool structuredOutputRequested)
    {
        try { return await content.ReadAsStreamAsync(timeoutToken); }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI provider stream timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming=true", selection.ProviderKey, selection.ModelKey, structuredOutputRequested);
            throw new AiProviderTimeoutException();
        }
    }

    private async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken timeoutToken, CancellationToken requestToken, AiProviderSelection selection, bool structuredOutputRequested)
    {
        try { return await reader.ReadLineAsync(timeoutToken); }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI provider stream timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}; StructuredOutput={StructuredOutput}; Streaming=true", selection.ProviderKey, selection.ModelKey, structuredOutputRequested);
            throw new AiProviderTimeoutException();
        }
    }

    private static JsonDocument ParseProviderJson(string body, AiProviderSelection selection, bool structuredOutputRequested, bool streaming)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException exception)
        {
            throw new AiProviderException(innerException: exception, failureCategory: AiProviderFailureCategories.MalformedResponse, modelKey: selection.ModelKey, structuredOutputRequested: structuredOutputRequested, streamingRequested: streaming);
        }
    }

    private static string? ReadOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString();
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString();
            }
        }
        return null;
    }

    private static Uri BuildResponsesUrl(string baseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
        return new Uri($"{normalized}/responses", UriKind.Absolute);
    }

    private static object[] BuildMultimodalContent(string text, IReadOnlyList<AiFileContext> attachments)
    {
        var content = new List<object> { new { type = "input_text", text } };
        foreach (var attachment in attachments.Where(item => !string.IsNullOrWhiteSpace(item.DataUrl)))
            content.Add(new { type = "input_image", image_url = attachment.DataUrl });
        return content.ToArray();
    }

    private static AiUsageMetadata ReadUsage(JsonElement root, AiProviderSelection selection, long latencyMs)
    {
        var response = root.TryGetProperty("response", out var responseElement) ? responseElement : root;
        var usage = response.TryGetProperty("usage", out var usageElement) ? usageElement : default;
        var input = ReadInt(usage, "input_tokens");
        var output = ReadInt(usage, "output_tokens");
        var cached = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens_details", out var details)
            ? ReadInt(details, "cached_tokens") : null;
        var status = response.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        return new AiUsageMetadata(selection.ProviderKey, selection.ModelKey, input, cached, output, null, null, (int)Math.Min(int.MaxValue, latencyMs), status ?? "completed", false);
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;
    }

    private static async Task<string?> ReadSafeProviderErrorAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken));
            return ReadSafeProviderError(document.RootElement);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private static string? ReadSafeProviderError(JsonElement root)
    {
        var candidate = root;
        if (root.TryGetProperty("error", out var error)) candidate = error;
        foreach (var property in new[] { "code", "type" })
        {
            if (!candidate.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            var safe = new string(text.Where(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.').ToArray());
            if (safe.Length > 0) return safe[..Math.Min(96, safe.Length)];
        }
        return null;
    }

    private static string ClassifyFailure(int statusCode) => statusCode switch
    {
        401 or 403 => AiProviderFailureCategories.Configuration,
        400 or 404 or 405 or 409 or 422 => AiProviderFailureCategories.UnsupportedRequest,
        408 or 425 or 429 => AiProviderFailureCategories.RateLimited,
        >= 500 => AiProviderFailureCategories.Transient,
        _ => AiProviderFailureCategories.Unknown,
    };
}

public static class AiProviderFailureCategories
{
    public const string Configuration = "configuration";
    public const string UnsupportedRequest = "unsupported_request";
    public const string RateLimited = "rate_limited";
    public const string Transient = "transient";
    public const string MalformedResponse = "malformed_response";
    public const string Unknown = "unknown";
}

public sealed class AiProviderException : Exception
{
    public AiProviderException(
        int? httpStatusCode = null,
        string? providerErrorCode = null,
        string failureCategory = AiProviderFailureCategories.Unknown,
        string? modelKey = null,
        bool structuredOutputRequested = false,
        bool streamingRequested = false,
        Exception? innerException = null) : base("The configured AI provider failed.", innerException)
    {
        HttpStatusCode = httpStatusCode;
        ProviderErrorCode = providerErrorCode;
        FailureCategory = failureCategory;
        ModelKey = modelKey;
        StructuredOutputRequested = structuredOutputRequested;
        StreamingRequested = streamingRequested;
    }

    public int? HttpStatusCode { get; }
    public string? ProviderErrorCode { get; }
    public string FailureCategory { get; }
    public string? ModelKey { get; }
    public bool StructuredOutputRequested { get; }
    public bool StreamingRequested { get; }
}

public sealed class AiProviderTimeoutException() : Exception("The configured AI provider timed out.");
