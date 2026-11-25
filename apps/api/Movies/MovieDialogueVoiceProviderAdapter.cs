using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Voice;

namespace Taslim.Api.Movies;

/// <summary>
/// Reuses the existing Voice Studio provider boundary for Movie dialogue. The
/// adapter only maps Movie dialogue semantics into the generic buffered voice
/// request; provider authentication, HTTP, output validation, and usage evidence
/// remain owned by the configured generic provider.
/// </summary>
public sealed class MovieDialogueVoiceProviderAdapter(
    IVoiceGenerationProvider provider,
    IOptions<MovieDialogueVoiceOptions> movieOptions,
    IOptions<VoiceGenerationOptions> voiceOptions) : IMovieDialogueVoiceProvider
{
    private readonly MovieDialogueVoiceOptions movieSettings = movieOptions.Value;
    private readonly VoiceGenerationOptions voiceSettings = voiceOptions.Value;

    public string Key => provider.Key;

    public async Task<MovieDialogueVoiceProviderResult> GenerateAsync(
        MovieDialogueVoiceProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConfigured(request);

        var expectedDuration = request.EndMilliseconds - request.StartMilliseconds;
        var generated = await provider.GenerateAsync(
            new VoiceGenerationInput(
                request.MovieProjectId ?? Guid.Empty,
                null,
                request.Text.Trim(),
                request.Language.Trim().ToLowerInvariant(),
                VoiceGenerationValues.Neutral,
                VoiceGenerationValues.Clear,
                BuildInstructions(request.DeliveryNotes),
                request.SpeakerName.Trim()),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var duration = generated.DurationMilliseconds is > 0
            ? generated.DurationMilliseconds.Value
            : (long)expectedDuration;
        if (duration <= 0 || duration > Math.Max(1, movieSettings.MaxDurationMilliseconds) || duration > int.MaxValue)
            throw new MovieDialogueVoiceOutputInvalidException();

        var contentType = generated.ContentType?.Trim().ToLowerInvariant() ?? string.Empty;
        var format = generated.Format?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
        if (generated.Content.Length <= 0
            || string.IsNullOrWhiteSpace(contentType)
            || !contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(format))
            throw new MovieDialogueVoiceOutputInvalidException();

        var usage = generated.Usage;
        return new MovieDialogueVoiceProviderResult(
            generated.Content,
            contentType,
            format,
            (int)duration,
            new MovieDialogueVoiceProviderUsage(
                string.IsNullOrWhiteSpace(usage.ModelKey) ? provider.Key : usage.ModelKey,
                usage.InputCharacters ?? request.Text.Length,
                usage.OutputBytes ?? generated.Content.Length,
                Math.Max(0, usage.LatencyMs),
                usage.EstimatedCostUsd,
                usage.ActualCostUsd,
                usage.Currency,
                usage.CostBasis,
                usage.SafeMetadataJson));
    }

    private void EnsureConfigured(MovieDialogueVoiceProviderRequest request)
    {
        if (!movieSettings.Enabled
            || !voiceSettings.Enabled
            || !string.Equals(movieSettings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(voiceSettings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(voiceSettings.Model)
            || string.Equals(voiceSettings.Model, "unconfigured", StringComparison.OrdinalIgnoreCase))
            throw new MovieDialogueVoiceProviderUnavailableException();
        if (!MovieDialogueLanguages.Supported.Contains(request.Language?.Trim() ?? string.Empty)
            || string.IsNullOrWhiteSpace(request.Text)
            || request.Text.Length > Math.Max(1, movieSettings.MaxTextCharacters)
            || request.Text.Length > Math.Max(1, voiceSettings.MaxProviderTextCharacters)
            || request.StartMilliseconds < 0
            || request.EndMilliseconds <= request.StartMilliseconds
            || request.EndMilliseconds - request.StartMilliseconds > Math.Max(1, movieSettings.MaxDurationMilliseconds))
            throw new MovieDialogueVoiceRequestValidationException(
                GenerationJobErrorCodes.MovieDialogueVoiceRequestInvalid,
                "The dialogue voice request is invalid.");
        if (request.DeliveryNotes?.Length > Math.Max(0, movieSettings.MaxDeliveryNotesCharacters))
            throw new MovieDialogueVoiceRequestValidationException(
                GenerationJobErrorCodes.MovieDialogueVoiceRequestInvalid,
                "The dialogue delivery notes are too long.");
    }

    private string? BuildInstructions(string? deliveryNotes)
    {
        if (string.IsNullOrWhiteSpace(deliveryNotes)) return null;
        var value = $"Dialogue delivery notes: {deliveryNotes.Trim()}";
        var maximum = Math.Max(1, voiceSettings.MaxInstructionsCharacters);
        return value.Length <= maximum ? value : value[..maximum];
    }
}
