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
            ["stream"] = true,
        };
        if (request.MaxOutputTokens is { } maxOutputTokens) requestPayload["max_output_tokens"] = maxOutputTokens;
        if (request.JsonMode) requestPayload["text"] = new { format = new { type = "json_object" } };
        message.Content = JsonContent.Create(requestPayload);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI provider timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}", selection.ProviderKey, selection.ModelKey);
            throw new AiProviderTimeoutException();
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "OpenAI provider request failed. ProviderKey={ProviderKey}; ModelKey={ModelKey}", selection.ProviderKey, selection.ModelKey);
            throw new AiProviderException();
        }

        using (response.Content)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OpenAI provider returned HTTP {StatusCode}. ProviderKey={ProviderKey}; ModelKey={ModelKey}", (int)response.StatusCode, selection.ProviderKey, selection.ModelKey);
                throw new AiProviderException();
            }

            await using var stream = await OpenResponseStreamAsync(response.Content, timeout.Token, cancellationToken, selection);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var data = new StringBuilder();
            AiUsageMetadata? usage = null;
            while (!reader.EndOfStream)
            {
                var line = await ReadLineAsync(reader, timeout.Token, cancellationToken, selection);
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
                using var json = JsonDocument.Parse(payload);
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
                    logger.LogWarning("OpenAI provider emitted a failure event. ProviderKey={ProviderKey}; ModelKey={ModelKey}; EventType={EventType}", selection.ProviderKey, selection.ModelKey, type);
                    throw new AiProviderException();
                }
            }

            if (usage is null) throw new AiProviderException();
            yield return new AiMessageCompleted(usage);
        }
    }

    private async Task<Stream> OpenResponseStreamAsync(HttpContent content, CancellationToken timeoutToken, CancellationToken requestToken, AiProviderSelection selection)
    {
        try { return await content.ReadAsStreamAsync(timeoutToken); }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI provider stream timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}", selection.ProviderKey, selection.ModelKey);
            throw new AiProviderTimeoutException();
        }
    }

    private async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken timeoutToken, CancellationToken requestToken, AiProviderSelection selection)
    {
        try { return await reader.ReadLineAsync(timeoutToken); }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI provider stream timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}", selection.ProviderKey, selection.ModelKey);
            throw new AiProviderTimeoutException();
        }
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
        {
            content.Add(new { type = "input_image", image_url = attachment.DataUrl });
        }
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
}

public sealed class AiProviderException() : Exception("The configured AI provider failed.");
public sealed class AiProviderTimeoutException() : Exception("The configured AI provider timed out.");
