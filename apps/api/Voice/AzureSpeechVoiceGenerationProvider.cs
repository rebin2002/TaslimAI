using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Voice;

/// <summary>
/// Azure AI Speech text-to-speech adapter. Azure identifiers stay inside this
/// server-side mapping and never cross the Voice Studio contract.
/// </summary>
public sealed class AzureSpeechVoiceGenerationProvider(
    HttpClient httpClient,
    IOptions<VoiceGenerationOptions> voiceOptions,
    ILogger<AzureSpeechVoiceGenerationProvider> logger) : IVoiceGenerationProvider
{
    private static readonly XNamespace SynthesisNamespace = "http://www.w3.org/2001/10/synthesis";
    private static readonly XNamespace MicrosoftSpeechNamespace = "http://www.w3.org/2001/mstts";
    private static readonly IReadOnlyDictionary<string, AzureSpeechAudioFormat> AudioFormats =
        new Dictionary<string, AzureSpeechAudioFormat>(StringComparer.OrdinalIgnoreCase)
        {
            ["mp3"] = new("audio-24khz-160kbitrate-mono-mp3", "audio/mpeg", 24_000),
            ["ogg"] = new("ogg-24khz-16bit-mono-opus", "audio/ogg", 24_000),
            ["opus"] = new("ogg-24khz-16bit-mono-opus", "audio/ogg", 24_000),
            ["wav"] = new("riff-24khz-16bit-mono-pcm", "audio/wav", 24_000),
            ["webm"] = new("webm-24khz-16bit-mono-opus", "audio/webm", 24_000),
        };

    private readonly VoiceGenerationOptions settings = voiceOptions.Value;
    private readonly AzureSpeechOptions azure = voiceOptions.Value.AzureSpeech;

    public string Key => "azure-speech";

    public async Task<VoiceProviderResult> GenerateAsync(
        VoiceGenerationInput request,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled
            || !string.Equals(settings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
            || !azure.Enabled
            || string.IsNullOrWhiteSpace(azure.ApiKey)
            || (string.IsNullOrWhiteSpace(azure.Region) && string.IsNullOrWhiteSpace(azure.Endpoint)))
            throw new VoiceProviderUnavailableException();

        if (!VoiceGenerationValues.Languages.Contains(request.Language, StringComparer.OrdinalIgnoreCase)
            || !settings.SupportedLanguages.Contains(request.Language, StringComparer.OrdinalIgnoreCase))
            throw new VoiceLanguageUnsupportedException();
        if (request.Text.Length == 0 || request.Text.Length > Math.Max(1, settings.MaxProviderTextCharacters))
            throw new VoiceProviderUnsupportedRequestException();

        var format = NormalizeFormat(settings.ResponseFormat);
        if (!AudioFormats.TryGetValue(format, out var audioFormat))
            throw new VoiceProviderUnsupportedRequestException();

        var voice = AzureSpeechVoiceMapping.Resolve(request.Language, request.VoiceStyle);
        if (voice is null)
            throw new VoiceLanguageUnsupportedException();

        var endpoint = BuildEndpoint(azure);
        if (endpoint is null)
            throw new VoiceProviderConfigurationException();

        var ssml = BuildSsml(request, voice);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 5, 300)));
        var maximumAttempts = Math.Clamp(azure.MaxRetryAttempts, 0, 4);
        var stopwatch = Stopwatch.StartNew();

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            message.Headers.Add("Ocp-Apim-Subscription-Key", azure.ApiKey);
            message.Headers.Add("X-Microsoft-OutputFormat", audioFormat.ProviderFormat);
            message.Headers.UserAgent.Add(new ProductInfoHeaderValue(SanitizeUserAgent(azure.UserAgent), "1.0"));
            message.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Azure Speech voice provider timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}", Key, azure.ModelKey, request.Language);
                throw new VoiceProviderTimeoutException();
            }
            catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < maximumAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, timeout.Token, cancellationToken);
                    continue;
                }

                logger.LogWarning(exception, "Azure Speech voice provider request failed. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}", Key, azure.ModelKey, request.Language);
                throw new VoiceProviderUnavailableException();
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode) && attempt < maximumAttempts)
                    {
                        await DelayBeforeRetryAsync(attempt, timeout.Token, cancellationToken);
                        continue;
                    }

                    var normalized = NormalizeStatus(response.StatusCode);
                    logger.LogWarning("Azure Speech voice provider returned HTTP {StatusCode}. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}; NormalizedFailure={NormalizedFailure}",
                        (int)response.StatusCode, Key, azure.ModelKey, request.Language, normalized.GetType().Name);
                    throw normalized;
                }

                ReadOnlyMemory<byte> content;
                try
                {
                    content = await ReadBoundedAsync(response.Content, settings.MaxOutputBytes, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Azure Speech voice provider audio download timed out. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}", Key, azure.ModelKey, request.Language);
                    throw new VoiceProviderTimeoutException();
                }

                ValidateAudio(content, response.Content.Headers.ContentType?.MediaType, format);
                var (sampleRateHz, durationMilliseconds) = ReadAudioMetadata(content, format, audioFormat.SampleRateHz);
                var usage = BuildUsage(request, content.Length, stopwatch.ElapsedMilliseconds, format);
                logger.LogInformation("Azure Speech voice provider completed. ProviderKey={ProviderKey}; ModelKey={ModelKey}; Language={Language}; Format={Format}; SizeBytes={SizeBytes}; DurationMs={DurationMs}",
                    Key, azure.ModelKey, request.Language, format, content.Length, durationMilliseconds);
                return new VoiceProviderResult(content, audioFormat.ContentType, format, durationMilliseconds, sampleRateHz, usage);
            }
        }
    }

    private VoiceProviderUsage BuildUsage(VoiceGenerationInput request, int outputBytes, long latencyMs, string format)
    {
        var inputCharacters = request.Text.Length;
        var estimatedCost = settings.PricingUsdPerMillionCharacters is { } price && price >= 0m
            ? decimal.Round(inputCharacters * price / 1_000_000m, 8, MidpointRounding.AwayFromZero)
            : (decimal?)null;
        var pricingSnapshot = settings.PricingUsdPerMillionCharacters is { } configuredPrice
            ? JsonSerializer.Serialize(new { unit = "USD per 1M characters", price = configuredPrice })
            : null;
        var safeMetadata = JsonSerializer.Serialize(new
        {
            inputCharacters,
            outputBytes,
            format,
            locale = AzureSpeechVoiceMapping.LocaleFor(request.Language),
        });
        return new VoiceProviderUsage(
            string.IsNullOrWhiteSpace(azure.ModelKey) ? "azure-speech-neural" : azure.ModelKey.Trim(),
            inputCharacters,
            outputBytes,
            ActualCostUsd: null,
            Math.Max(1, (int)Math.Min(int.MaxValue, latencyMs)),
            PricingVersion: string.IsNullOrWhiteSpace(settings.PricingVersion) ? null : settings.PricingVersion,
            PricingSnapshotJson: pricingSnapshot,
            Currency: settings.Currency,
            CostBasis: estimatedCost.HasValue ? UsageCostBasis.Estimated : null,
            SafeMetadataJson: safeMetadata,
            EstimatedCostUsd: estimatedCost);
    }

    private static string BuildSsml(VoiceGenerationInput request, AzureSpeechVoice voice)
    {
        var spokenText = new XText(request.Text);
        var speechContent = new List<object>();
        if (voice.StyleFor(request.SpeakingStyle) is { } style)
            speechContent.Add(new XElement(MicrosoftSpeechNamespace + "express-as", new XAttribute("style", style), spokenText));
        else if (voice.RateFor(request.SpeakingStyle) is { } rate)
            speechContent.Add(new XElement(SynthesisNamespace + "prosody", new XAttribute("rate", rate), spokenText));
        else
            speechContent.Add(spokenText);

        var voiceElement = new XElement(SynthesisNamespace + "voice",
            new XAttribute("name", voice.ProviderVoiceId),
            speechContent);
        return new XDocument(
            new XElement(SynthesisNamespace + "speak",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xml + "lang", voice.Locale),
                new XAttribute(XNamespace.Xmlns + "mstts", MicrosoftSpeechNamespace),
                voiceElement)).ToString(SaveOptions.DisableFormatting);
    }

    private static Uri? BuildEndpoint(AzureSpeechOptions options)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(options.Endpoint))
            {
                var configured = new Uri(options.Endpoint.Trim(), UriKind.Absolute);
                if (configured.Scheme != Uri.UriSchemeHttps) return null;
                if (configured.AbsolutePath.EndsWith("/cognitiveservices/v1", StringComparison.OrdinalIgnoreCase)) return configured;
                return new Uri(configured, configured.AbsolutePath.TrimEnd('/') + "/cognitiveservices/v1");
            }

            var region = options.Region.Trim();
            if (region.Length == 0 || region.Contains('/') || region.Contains(':')) return null;
            return new Uri($"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1", UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken timeoutToken, CancellationToken callerToken)
    {
        var baseDelay = Math.Clamp(azure.RetryBaseDelayMilliseconds, 25, 10_000);
        var delay = Math.Min(30_000, baseDelay * Math.Pow(2, attempt));
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delay), timeoutToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new VoiceProviderTimeoutException();
        }
    }

    private static Exception NormalizeStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new VoiceProviderAuthenticationException(),
        HttpStatusCode.TooManyRequests => new VoiceProviderRateLimitException(),
        HttpStatusCode.RequestTimeout => new VoiceProviderTimeoutException(),
        HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType or (HttpStatusCode)422 => new VoiceProviderInvalidInputException(),
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => new VoiceProviderUnavailableException(),
        _ => new VoiceProviderFailureException(),
    };

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpContent content, int configuredMaximum, CancellationToken cancellationToken)
    {
        var maximum = Math.Min(10 * 1_048_576, Math.Max(1, configuredMaximum));
        if (content.Headers.ContentLength is > 0 and var contentLength && contentLength > maximum)
            throw new VoiceOutputInvalidException();

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximum) throw new VoiceOutputInvalidException();
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static void ValidateAudio(ReadOnlyMemory<byte> content, string? contentType, string format)
    {
        if (content.Length == 0 || !AudioFormats.TryGetValue(format, out var expected))
            throw new VoiceOutputInvalidException();
        if (!string.IsNullOrWhiteSpace(contentType) && !ContentTypeMatches(contentType, expected.ContentType))
            throw new VoiceOutputInvalidException();

        var bytes = content.Span;
        var validRepresentation = format switch
        {
            "mp3" => bytes.Length >= 2 && ((bytes.Length >= 3 && bytes[..3].SequenceEqual("ID3"u8)) || (bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0)),
            "wav" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8),
            "ogg" or "opus" => bytes.Length >= 4 && bytes[..4].SequenceEqual("OggS"u8),
            "webm" => bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }),
            _ => false,
        };
        if (!validRepresentation) throw new VoiceOutputInvalidException();
    }

    private static bool ContentTypeMatches(string actual, string expected) =>
        string.Equals(actual.Trim(), expected, StringComparison.OrdinalIgnoreCase)
        || expected == "audio/wav" && string.Equals(actual.Trim(), "audio/x-wav", StringComparison.OrdinalIgnoreCase);

    private static (int? SampleRateHz, long? DurationMilliseconds) ReadAudioMetadata(ReadOnlyMemory<byte> content, string format, int defaultSampleRateHz)
    {
        if (!string.Equals(format, "wav", StringComparison.OrdinalIgnoreCase)) return (defaultSampleRateHz, null);
        var bytes = content.Span;
        var fmtOffset = FindChunk(bytes, "fmt "u8);
        var dataOffset = FindChunk(bytes, "data"u8);
        if (fmtOffset < 0 || dataOffset < 0 || fmtOffset + 20 > bytes.Length || dataOffset + 8 > bytes.Length) return (defaultSampleRateHz, null);
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(fmtOffset + 12, 4));
        var byteRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(fmtOffset + 16, 4));
        var dataLength = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(dataOffset + 4, 4)), (uint)Math.Max(0, bytes.Length - dataOffset - 8));
        var duration = byteRate > 0 ? (long?)(dataLength * 1000L / byteRate) : null;
        return (sampleRate > 0 ? sampleRate : defaultSampleRateHz, duration);
    }

    private static int FindChunk(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> chunk)
    {
        for (var offset = 12; offset + 8 <= bytes.Length; )
        {
            if (bytes.Slice(offset, 4).SequenceEqual(chunk)) return offset;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (length > int.MaxValue || offset > bytes.Length - 8 - (int)length) break;
            offset += 8 + (int)length + ((length & 1) == 1 ? 1 : 0);
        }
        return -1;
    }

    private static string NormalizeFormat(string format) => string.IsNullOrWhiteSpace(format) ? "mp3" : format.Trim().ToLowerInvariant();

    private static string SanitizeUserAgent(string value)
    {
        var sanitized = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Trim().Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "TaslimAI-Voice" : sanitized;
    }

    private sealed record AzureSpeechAudioFormat(string ProviderFormat, string ContentType, int SampleRateHz);
}

internal sealed record AzureSpeechVoice(string Locale, string ProviderVoiceId, IReadOnlyDictionary<string, string> SpeakingStyles)
{
    public string? StyleFor(string speakingStyle) => SpeakingStyles.TryGetValue(speakingStyle, out var providerStyle) ? providerStyle : null;

    public string? RateFor(string speakingStyle) => string.Equals(speakingStyle, VoiceGenerationValues.Calm, StringComparison.OrdinalIgnoreCase) ? "-10%" : null;
}

internal static class AzureSpeechVoiceMapping
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, AzureSpeechVoice>> Voices =
        new Dictionary<string, IReadOnlyDictionary<string, AzureSpeechVoice>>(StringComparer.OrdinalIgnoreCase)
        {
            [VoiceGenerationValues.English] = new Dictionary<string, AzureSpeechVoice>(StringComparer.OrdinalIgnoreCase)
            {
                [VoiceGenerationValues.Neutral] = new("en-US", "en-US-JennyNeural", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [VoiceGenerationValues.Conversational] = "chat",
                    [VoiceGenerationValues.Expressive] = "cheerful",
                }),
                [VoiceGenerationValues.Warm] = new("en-US", "en-US-JennyNeural", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [VoiceGenerationValues.Conversational] = "friendly",
                    [VoiceGenerationValues.Expressive] = "cheerful",
                }),
                [VoiceGenerationValues.Professional] = new("en-US", "en-US-GuyNeural", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                [VoiceGenerationValues.Storytelling] = new("en-US", "en-US-ChristopherNeural", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            },
            [VoiceGenerationValues.Arabic] = new Dictionary<string, AzureSpeechVoice>(StringComparer.OrdinalIgnoreCase)
            {
                [VoiceGenerationValues.Neutral] = new("ar-SA", "ar-SA-ZariyahNeural", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                [VoiceGenerationValues.Warm] = new("ar-SA", "ar-SA-ZariyahNeural", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                [VoiceGenerationValues.Professional] = new("ar-SA", "ar-SA-HamedNeural", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                [VoiceGenerationValues.Storytelling] = new("ar-SA", "ar-SA-ZariyahNeural", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            },
        };

    public static AzureSpeechVoice? Resolve(string language, string voiceStyle) =>
        Voices.TryGetValue(language, out var languageVoices) && languageVoices.TryGetValue(voiceStyle, out var voice) ? voice : null;

    public static string? LocaleFor(string language) =>
        Voices.TryGetValue(language, out var languageVoices)
            ? languageVoices.Values.FirstOrDefault()?.Locale
            : null;
}
