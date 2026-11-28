namespace Taslim.Api.Music;

public sealed class MusicGenerationOptions
{
    public bool Enabled { get; set; }
    public string ProviderKey { get; set; } = "unconfigured";
    public string Model { get; set; } = "unconfigured";
    public int ProviderTimeoutSeconds { get; set; } = 300;
    public int MaxPromptCharacters { get; set; } = 4_000;
    public int MaxPurposeCharacters { get; set; } = 1_000;
    public int MaxAdditionalInstructionsCharacters { get; set; } = 2_000;
    public int MaxTitleCharacters { get; set; } = 160;
    public int MinDurationSeconds { get; set; } = 15;
    public int MaxDurationSeconds { get; set; } = 600;
    public int MaxOutputBytes { get; set; } = 25 * 1_048_576;
    public string PricingVersion { get; set; } = "music-provider-unconfigured";
    public string Currency { get; set; } = "USD";

    // Mubert AI Music API v3 public track-generation contract. These remain
    // disabled and empty until a production Mubert account is configured.
    public string MubertApiBaseUrl { get; set; } = "https://music-api.mubert.com/api/v3/public/";
    public string MubertCustomerId { get; set; } = string.Empty;
    public string MubertAccessToken { get; set; } = string.Empty;
    public int MubertPollIntervalMilliseconds { get; set; } = 1_000;
    public int MubertMaxPollAttempts { get; set; } = 300;
    public int MubertMaxRetryAttempts { get; set; } = 2;
    public int MubertRetryBaseDelayMilliseconds { get; set; } = 250;
    public int MubertBitrate { get; set; } = 256;
    public string MubertFormat { get; set; } = "mp3";
    public string MubertIntensity { get; set; } = "high";
    public string MubertMode { get; set; } = "track";

    // Stable Audio API v2beta text-to-audio contract. These remain disabled and
    // empty until a server-only Stability API key is configured through the
    // deployment environment or secret configuration store.
    public string StableAudioApiBaseUrl { get; set; } = "https://api.stability.ai/";
    public string StableAudioApiKey { get; set; } = string.Empty;
    public string StableAudioModel { get; set; } = "stable-audio-3";
    public string StableAudioOutputFormat { get; set; } = "mp3";
    public int StableAudioPollIntervalMilliseconds { get; set; } = 10_000;
    public int StableAudioMaxPollAttempts { get; set; } = 36;
    public int StableAudioMaxRetryAttempts { get; set; } = 2;
    public int StableAudioRetryBaseDelayMilliseconds { get; set; } = 500;
    public int StableAudioCreditsPerGeneration { get; set; } = 26;
    public decimal StableAudioUsdPerCredit { get; set; } = 0.01m;
}

public class MusicProviderUnavailableException : Exception
{
    public MusicProviderUnavailableException() : base("The configured music provider is unavailable.")
    {
    }

    protected MusicProviderUnavailableException(string message) : base(message)
    {
    }
}
public sealed class MusicProviderAuthenticationException() : MusicProviderUnavailableException;
public sealed class MusicProviderQuotaException() : MusicProviderUnavailableException;
public sealed class MusicProviderTimeoutException() : Exception("The music provider timed out.");
public sealed class MusicProviderRateLimitException() : Exception("The music provider request was rate limited.");
public sealed class MusicProviderRejectedException() : Exception("The music provider rejected the music request.");
public sealed class MusicProviderInvalidRequestException() : Exception("The music provider rejected the music request as invalid.");
public sealed class MusicProviderFailureException() : Exception("The music provider failed.");
public sealed class MusicOutputInvalidException() : Exception("The music provider returned an invalid music file.");
public sealed class MusicRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MusicOutputStorageException() : Exception("The generated music could not be saved.");

/// <summary>
/// The provider completed and billed a generation or track, but its finished result could not be
/// retrieved even after the bounded GET re-fetches. Deriving from
/// <see cref="MusicProviderUnavailableException"/> keeps every existing provider-unavailable guard
/// and catch working unchanged, while the distinct type lets the failure be mapped to its own
/// public code so that it is NOT treated as an ordinary "please try again" provider fault: a
/// retry would POST a second billable generation and orphan the one that already exists.
/// Nothing provider-specific (URLs, keys, payloads) is carried in the message.
/// </summary>
public sealed class MusicOutputRetrievalFailedException : MusicProviderUnavailableException
{
    public MusicOutputRetrievalFailedException() : base("The completed music result could not be retrieved.")
    {
    }
}

/// <summary>
/// The provider accepted a billable generation - it answered the create call with a track or
/// generation id - but its outcome was never observed: polling timed out, ran out of bounded
/// attempts, or kept failing transiently, so it is unknown whether the track finished and whether
/// it was charged. This is deliberately NOT the retrieval-failure class, whose copy states that
/// the music was generated: nothing here establishes that. It maps to the public code the lease
/// recovery already uses for the same unknown outcome, so the customer sees one truthful message
/// and no automatic path re-posts; the accepted provider operation id stays in the log for
/// reconciliation. Deriving from <see cref="MusicProviderUnavailableException"/> keeps the existing
/// provider-unavailable guards and catches working unchanged. Nothing provider-specific (URLs,
/// keys, payloads) is carried in the message.
/// </summary>
public sealed class MusicProviderSubmissionUnconfirmedException : MusicProviderUnavailableException
{
    public MusicProviderSubmissionUnconfirmedException()
        : base("The music provider accepted the generation, but its outcome could not be confirmed.")
    {
    }
}

/// <summary>
/// F-36: the member (or the host) cancelled a music job AFTER the provider had already accepted the
/// billable generation - it answered the create call with a track or generation id - and this
/// integration establishes no provider-side cancellation, so the accepted operation may still be
/// running, may still finish and may still bill. Deriving from <see
/// cref="OperationCanceledException"/> keeps the worker's cancellation handling intact (the job still
/// ends as Cancelled, because that is what the member asked for), while the distinct type lets the
/// worker record WHICH cancellation it was: an ordinary pre-submission cancellation, which is safe to
/// retry, is a plain cancellation and never becomes this class. The accepted operation id is carried
/// for logs and reconciliation only, and nothing provider-sensitive is in the message.
/// </summary>
public sealed class MusicProviderCancellationWithAcceptedSubmissionException : OperationCanceledException
{
    public MusicProviderCancellationWithAcceptedSubmissionException(string? providerOperationId, bool outputConfirmedComplete)
        : base("The music generation was cancelled after the provider accepted it, so its outcome is unconfirmed.")
    {
        ProviderOperationId = providerOperationId;
        OutputConfirmedComplete = outputConfirmedComplete;
    }

    /// <summary>Accepted provider operation id (Mubert track id / Stable generation id), log-only.</summary>
    public string? ProviderOperationId { get; }

    /// <summary>
    /// True when polling had already seen the finished result, so only delivery is unknown (the same
    /// posture as the retrieval-failure family); false when the accepted generation was still running,
    /// so completion and charging are both unknown.
    /// </summary>
    public bool OutputConfirmedComplete { get; }
}
