using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Images;

public sealed record ImageProviderUsage(
    int? InputTokens,
    int? TextInputTokens,
    int? ImageInputTokens,
    int? OutputTokens,
    decimal? ActualCostUsd,
    string FinishReason = "completed",
    int LatencyMs = 0,
    int? ImageOutputTokens = null,
    string CostBasis = UsageCostBasis.Estimated);

public sealed record ImageProviderResult(
    ReadOnlyMemory<byte> Content,
    string ContentType,
    string Format,
    int? Width,
    int? Height,
    ImageProviderUsage Usage);

public interface IImageGenerationProvider
{
    string Key { get; }
    Task<ImageProviderResult> GenerateAsync(
        ImageGenerationInput request,
        ImagePromptBuildResult prompt,
        CancellationToken cancellationToken = default);
}

public static class ImageGenerationCostEstimator
{
    public static decimal Estimate(ImagePromptBuildResult prompt, ImagePricingOptions pricing)
    {
        var textTokens = Math.Max(1, (prompt.Prompt.Length + 3) / 4);
        var outputTokens = prompt.NormalizedAspectRatio switch
        {
            ImageGenerationValues.Portrait => prompt.NormalizedQuality == ImageGenerationValues.High ? 6240 : 1584,
            ImageGenerationValues.Landscape => prompt.NormalizedQuality == ImageGenerationValues.High ? 6208 : 1568,
            _ => prompt.NormalizedQuality == ImageGenerationValues.High ? 4160 : 1056,
        };
        return decimal.Round(
            textTokens * pricing.TextInputUsdPerMillion / 1_000_000m
            + outputTokens * pricing.ImageOutputUsdPerMillion / 1_000_000m,
            8,
            MidpointRounding.AwayFromZero);
    }
}

public sealed class OpenAiImageGenerationProvider(
    HttpClient httpClient,
    IOptions<AiOptions> aiOptions,
    IOptions<ImageGenerationOptions> imageOptions,
    ILogger<OpenAiImageGenerationProvider> logger) : IImageGenerationProvider
{
    private readonly AiOptions aiSettings = aiOptions.Value;
    private readonly ImageGenerationOptions settings = imageOptions.Value;

    public string Key => "openai";

    public async Task<ImageProviderResult> GenerateAsync(
        ImageGenerationInput request,
        ImagePromptBuildResult prompt,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled || !string.Equals(settings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
            || !aiSettings.OpenAI.Enabled || string.IsNullOrWhiteSpace(aiSettings.OpenAI.ApiKey))
            throw new ImageProviderUnavailableException();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 15, 300)));
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildImagesUrl(aiSettings.OpenAI.BaseUrl));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aiSettings.OpenAI.ApiKey);
        message.Content = JsonContent.Create(new
        {
            model = settings.Model,
            prompt = prompt.Prompt,
            n = 1,
            size = ToProviderSize(prompt.NormalizedAspectRatio),
            quality = ToProviderQuality(prompt.NormalizedQuality),
            output_format = "png",
            background = "opaque",
            moderation = "auto",
        });

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI image provider timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}", Key, settings.Model);
            throw new ImageProviderTimeoutException();
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "OpenAI image provider request failed. ProviderKey={ProviderKey}; ModelKey={ModelKey}", Key, settings.Model);
            throw new ImageProviderFailureException();
        }

        using (response)
        {
            var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = ReadError(body);
                logger.LogWarning("OpenAI image provider returned HTTP {StatusCode}. ErrorCode={ErrorCode}; RequestId={RequestId}", (int)response.StatusCode, error.Code ?? "unknown", requestId ?? "none");
                if (string.Equals(error.Code, "moderation_blocked", StringComparison.OrdinalIgnoreCase)) throw new ImageProviderSafetyException();
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    throw new ImageProviderUnavailableException();
                throw new ImageProviderFailureException();
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var data = root.GetProperty("data");
                if (data.GetArrayLength() != 1) throw new ImageOutputInvalidException();
                var encoded = data[0].GetProperty("b64_json").GetString();
                if (string.IsNullOrWhiteSpace(encoded)) throw new ImageOutputInvalidException();
                var bytes = Convert.FromBase64String(encoded);
                var info = ImageBinaryInspector.Read(bytes);
                if (info is null || !string.Equals(info.ContentType, "image/png", StringComparison.OrdinalIgnoreCase)) throw new ImageOutputInvalidException();
                var usage = ReadUsage(root, prompt, stopwatch.ElapsedMilliseconds);
                logger.LogInformation("OpenAI image provider completed. ProviderKey={ProviderKey}; ModelKey={ModelKey}; DurationMs={DurationMs}; RequestId={RequestId}", Key, settings.Model, stopwatch.ElapsedMilliseconds, requestId ?? "none");
                return new ImageProviderResult(bytes, info.ContentType, info.Format, info.Width, info.Height, usage);
            }
            catch (FormatException) { throw new ImageOutputInvalidException(); }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "OpenAI image provider returned an unreadable response. RequestId={RequestId}", requestId ?? "none");
                throw new ImageOutputInvalidException();
            }
        }
    }

    private ImageProviderUsage ReadUsage(JsonElement root, ImagePromptBuildResult prompt, long latencyMs)
    {
        var usageElement = root.TryGetProperty("usage", out var value) ? value : default;
        var input = ReadInt(usageElement, "input_tokens");
        var output = ReadInt(usageElement, "output_tokens");
        var details = usageElement.ValueKind == JsonValueKind.Object && usageElement.TryGetProperty("input_tokens_details", out var inputDetails) ? inputDetails : default;
        var outputDetails = usageElement.ValueKind == JsonValueKind.Object && usageElement.TryGetProperty("output_tokens_details", out var outputTokenDetails) ? outputTokenDetails : default;
        var textInput = ReadInt(details, "text_tokens");
        var imageInput = ReadInt(details, "image_tokens");
        var imageOutput = ReadInt(outputDetails, "image_tokens");
        var actualCost = CalculateCost(input, textInput, imageInput, output, imageOutput, prompt);
        var costBasis = input.HasValue && output.HasValue ? UsageCostBasis.Actual : UsageCostBasis.Estimated;
        return new ImageProviderUsage(input, textInput, imageInput, output, actualCost, "completed", Math.Max(1, (int)Math.Min(int.MaxValue, latencyMs)), imageOutput, costBasis);
    }

    private decimal CalculateCost(int? input, int? textInput, int? imageInput, int? output, int? imageOutput, ImagePromptBuildResult prompt)
    {
        var textTokens = Math.Max(0, textInput ?? input ?? EstimateTextTokens(prompt.Prompt));
        var imageTokens = Math.Max(0, imageInput ?? 0);
        // The Images API currently reports billable image output through normal output_tokens.
        // Keep ImageOutputTokens null unless output_tokens_details.image_tokens is present.
        var outputTokens = Math.Max(0, imageOutput ?? output ?? EstimateOutputTokens(prompt.NormalizedAspectRatio, prompt.NormalizedQuality));
        return decimal.Round(
            textTokens * settings.Pricing.TextInputUsdPerMillion / 1_000_000m
            + imageTokens * settings.Pricing.ImageInputUsdPerMillion / 1_000_000m
            + outputTokens * settings.Pricing.ImageOutputUsdPerMillion / 1_000_000m,
            8,
            MidpointRounding.AwayFromZero);
    }

    private static int EstimateTextTokens(string prompt) => Math.Max(1, (prompt.Length + 3) / 4);

    private static int EstimateOutputTokens(string aspect, string quality)
    {
        var high = string.Equals(quality, ImageGenerationValues.High, StringComparison.OrdinalIgnoreCase);
        return aspect switch
        {
            ImageGenerationValues.Portrait => high ? 6240 : 1584,
            ImageGenerationValues.Landscape => high ? 6208 : 1568,
            _ => high ? 4160 : 1056,
        };
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static (string? Code, string? Type) ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.TryGetProperty("error", out var element) ? element : default;
            var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
            var type = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            return (code, type);
        }
        catch (JsonException) { return (null, null); }
    }

    private static Uri BuildImagesUrl(string baseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
        return new Uri($"{normalized}/images/generations", UriKind.Absolute);
    }

    private static string ToProviderSize(string aspect) => aspect switch
    {
        ImageGenerationValues.Portrait => "1024x1536",
        ImageGenerationValues.Landscape => "1536x1024",
        _ => "1024x1024",
    };

    private static string ToProviderQuality(string quality) => quality.Equals(ImageGenerationValues.High, StringComparison.OrdinalIgnoreCase) ? "high" : "medium";
}

public sealed record ImageBinaryInfo(string ContentType, string Format, int? Width, int? Height);

public static class ImageBinaryInspector
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageBinaryInfo? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(PngSignature))
            return new ImageBinaryInfo("image/png", "png", ReadBigEndianInt(bytes[16..20]), ReadBigEndianInt(bytes[20..24]));
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return ReadWebp(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ReadJpeg(bytes);
        return null;
    }

    private static ImageBinaryInfo? ReadWebp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 30 && bytes[12..16].SequenceEqual("VP8X"u8))
        {
            var width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
            var height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
            return new ImageBinaryInfo("image/webp", "webp", width, height);
        }
        return new ImageBinaryInfo("image/webp", "webp", null, null);
    }

    private static ImageBinaryInfo? ReadJpeg(ReadOnlySpan<byte> bytes)
    {
        var index = 2;
        while (index + 9 < bytes.Length)
        {
            if (bytes[index] != 0xFF) { index++; continue; }
            var marker = bytes[index + 1];
            index += 2;
            if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7) continue;
            if (index + 2 > bytes.Length) break;
            var length = (bytes[index] << 8) + bytes[index + 1];
            if (length < 2 || index + length > bytes.Length) break;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                return new ImageBinaryInfo("image/jpeg", "jpeg", (bytes[index + 5] << 8) + bytes[index + 6], (bytes[index + 3] << 8) + bytes[index + 4]);
            }
            index += length;
        }
        return new ImageBinaryInfo("image/jpeg", "jpeg", null, null);
    }

    private static int ReadBigEndianInt(ReadOnlySpan<byte> value) => (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
}
