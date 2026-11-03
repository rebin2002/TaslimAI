namespace Taslim.Api.Music;

/// <summary>
/// Describes capabilities implemented by the configured Taslim adapter, not
/// merely features mentioned in an upstream product page.
/// </summary>
public sealed record MusicGenerationProviderCapabilities(
    bool SupportsInstrumental,
    bool SupportsLyricsToVocals,
    bool SupportsAudioReferenceConditioning,
    bool SupportsVideoAudioReference,
    bool SupportsAuthorizedCoverOrRearrangement,
    bool SupportsRealSingerVoiceCloning,
    IReadOnlyList<string> SupportedLyricsLanguages,
    IReadOnlyList<string> SupportedLyricsDialects);

/// <summary>
/// Unconfigured providers expose no capabilities. Unknown provider keys retain
/// the legacy instrumental path but gain no new song/reference capabilities;
/// new adapters must be reviewed before a new mode can pass validation.
/// </summary>
public static class MusicGenerationCapabilityCatalog
{
    private static readonly MusicGenerationProviderCapabilities InstrumentalOnly = new(
        SupportsInstrumental: true,
        SupportsLyricsToVocals: false,
        SupportsAudioReferenceConditioning: false,
        SupportsVideoAudioReference: false,
        SupportsAuthorizedCoverOrRearrangement: false,
        SupportsRealSingerVoiceCloning: false,
        SupportedLyricsLanguages: Array.Empty<string>(),
        SupportedLyricsDialects: Array.Empty<string>());

    private static readonly MusicGenerationProviderCapabilities Unsupported = new(
        SupportsInstrumental: false,
        SupportsLyricsToVocals: false,
        SupportsAudioReferenceConditioning: false,
        SupportsVideoAudioReference: false,
        SupportsAuthorizedCoverOrRearrangement: false,
        SupportsRealSingerVoiceCloning: false,
        SupportedLyricsLanguages: Array.Empty<string>(),
        SupportedLyricsDialects: Array.Empty<string>());

    private static readonly MusicGenerationProviderCapabilities LegacyInstrumentalOnly = new(
        SupportsInstrumental: true,
        SupportsLyricsToVocals: false,
        SupportsAudioReferenceConditioning: false,
        SupportsVideoAudioReference: false,
        SupportsAuthorizedCoverOrRearrangement: false,
        SupportsRealSingerVoiceCloning: false,
        SupportedLyricsLanguages: Array.Empty<string>(),
        SupportedLyricsDialects: Array.Empty<string>());

    public static MusicGenerationProviderCapabilities ForProvider(string? providerKey) =>
        (providerKey?.Trim().ToLowerInvariant()) switch
        {
            "mubert" => InstrumentalOnly,
            "stable-audio" => InstrumentalOnly,
            // SunoAPI.org is a third-party service. Its public materials do not
            // establish Taslim's multi-tenant rights or dialect quality; no
            // reviewed adapter is currently available.
            "suno" or "sunoapi-org" => Unsupported,
            // The API integration tests replace this key with a deterministic
            // local fake; it is not an upstream or production capability.
            "test" => InstrumentalOnly,
            null or "" or "unconfigured" => Unsupported,
            _ => LegacyInstrumentalOnly,
        };
}
