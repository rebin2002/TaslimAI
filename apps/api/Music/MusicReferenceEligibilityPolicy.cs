namespace Taslim.Api.Music;

/// <summary>
/// Verification provenance of a single piece of upstream evidence. The distinction is the whole
/// point of the type: only <see cref="VerifiedByExistingService"/> can ever satisfy a rule, and a
/// denial recorded by a service is reported separately from an unverified claim so operators can
/// tell "refused" from "never checked".
/// </summary>
public enum MusicEvidenceVerificationState
{
    /// No upstream verification exists. Never sufficient; always denies.
    NotVerified = 0,

    /// A service that already owns this fact verified it and issued a record.
    VerifiedByExistingService = 1,

    /// A service that already owns this fact actively refused it.
    VerifiedDenial = 2,
}

/// <summary>
/// What the caller wants to reuse from the referenced audio/video material. A declared intent can
/// only ever raise the evidentiary bar, never lower it: naming a real singer while declaring a
/// milder intent is refused for review rather than reclassified, and an imitation intent still
/// requires a provider that can enforce the recorded right-to-use limit.
/// </summary>
public enum MusicReferenceIntent
{
    /// Reuse genre, instrumentation, tempo or mood only.
    StyleConditioning = 0,

    /// Reuse melodic contour. Accepted only where the provider supports it and authorization is verified.
    MelodyResemblance = 1,

    /// Cover or rearrangement of a referenced work.
    AuthorizedCoverOrRearrangement = 2,

    /// Make the output sound like an identifiable, named singer's voice.
    NamedVoiceImitation = 3,
}

/// <summary>A reference the caller asked to use, as a bare claim: media kind plus asset identifier.
/// A selector is never evidence of anything; it is only the subject the evidence records talk about.</summary>
public sealed record MusicReferenceAssetSelector(
    string? MediaType,
    Guid AssetId);

/// <summary>
/// Who is asking, and whether the services that already own authentication and workspace
/// membership vouch for it. Personal-workspace self-membership still has to be a recorded fact:
/// an object containing two identifiers is not a session.
/// </summary>
public sealed record MusicReferencePrincipalEvidence(
    Guid UserId,
    Guid RequestWorkspaceId,
    MusicEvidenceVerificationState SessionVerification = MusicEvidenceVerificationState.NotVerified,
    MusicEvidenceVerificationState WorkspaceMembershipVerification = MusicEvidenceVerificationState.NotVerified,
    string? VerifiedByService = null);

/// <summary>
/// Whether the private asset exists and may be read from the workspace making the request. Supplied
/// by the asset/storage authorization path. The absence of this record is the "user pasted an asset
/// id" case, and it denies: an identifier is an address, not an access grant.
/// </summary>
public sealed record MusicReferenceAssetAccessEvidence(
    Guid AssetId,
    Guid AssetWorkspaceId,
    MusicEvidenceVerificationState AssetLookupVerification = MusicEvidenceVerificationState.NotVerified,
    MusicEvidenceVerificationState WorkspaceAccessVerification = MusicEvidenceVerificationState.NotVerified,
    string? VerifiedByService = null);

/// <summary>
/// Server-side probe facts for a referenced asset: container, MIME, size, duration and whether an
/// audio track is actually present. Filled in by media inspection, never by the client's file name.
/// </summary>
public sealed record MusicReferenceMediaProbeEvidence(
    Guid AssetId,
    string? ContainerName,
    string? MimeType,
    long SizeBytes,
    double DurationSeconds,
    bool AudioTrackDetected,
    MusicEvidenceVerificationState ProbeVerification = MusicEvidenceVerificationState.NotVerified,
    string? VerifiedByService = null);

/// <summary>
/// Rights evidence for the reference. IMPORTANT: a ticked box in the request body is not a copyright
/// determination and this policy does not treat it as one. An attestation is only usable when an
/// attestation service stored it and can be named. <see cref="PresentedAsClientCheckboxOnly"/> lets
/// an adapter honestly describe a checkbox so it is refused loudly instead of silently laundered
/// into a verified record.
/// </summary>
public sealed record MusicReferenceRightsEvidence(
    MusicEvidenceVerificationState ReferenceRightsAttestation = MusicEvidenceVerificationState.NotVerified,
    string? AttestationRecordId = null,
    MusicEvidenceVerificationState NamedVoiceRightToUse = MusicEvidenceVerificationState.NotVerified,
    string? RightToUseRecordId = null,
    bool PresentedAsClientCheckboxOnly = false);

/// <summary>
/// What the provider actually supports for authorized reference conditioning, as verified against
/// its documented contract. Instrumental-only providers record <c>false</c> here, and an unverified
/// provider identifier records <see cref="MusicEvidenceVerificationState.NotVerified"/>, so a
/// reference request fails closed instead of being silently downgraded to a text prompt.
/// </summary>
public sealed record MusicProviderReferenceCapabilityEvidence(
    string? ProviderKey,
    MusicEvidenceVerificationState CapabilityVerification = MusicEvidenceVerificationState.NotVerified,
    bool SupportsAuthorizedAudioReferenceConditioning = false,
    bool SupportsVideoDerivedAudioReference = false,
    bool SupportsAuthorizedCoverOrRearrangement = false,
    bool SupportsNamedVoiceRightToUseEnforcement = false,
    string? VerifiedByService = null);

/// <summary>The complete subject of one eligibility decision.</summary>
public sealed record MusicReferenceEligibilityRequest(
    MusicReferenceIntent Intent,
    MusicReferencePrincipalEvidence? Principal,
    IReadOnlyList<MusicReferenceAssetSelector> RequestedAssets,
    IReadOnlyList<MusicReferenceAssetAccessEvidence> AssetAccessEvidence,
    IReadOnlyList<MusicReferenceMediaProbeEvidence> MediaProbeEvidence,
    MusicReferenceRightsEvidence? Rights,
    MusicProviderReferenceCapabilityEvidence? ProviderCapability,
    string? NamedSinger = null);

/// <summary>
/// Bounds for a reference. Defaults mirror the shared upload ceiling and the ingest window already
/// used by Music generation, but the policy validates them so a degenerate configuration denies
/// rather than waving everything through.
/// </summary>
public sealed record MusicReferenceEligibilityLimits
{
    public long MaxSizeBytes { get; init; } = 25L * 1_048_576;

    public double MinDurationSeconds { get; init; } = 15;

    public double MaxDurationSeconds { get; init; } = 600;

    public int MaxReferenceAssets { get; init; } = 2;

    public IReadOnlyCollection<string> SupportedContainers { get; init; } =
        new[] { "mp3", "wav", "m4a", "flac", "ogg", "mp4", "mov" };

    public IReadOnlyCollection<string> SupportedMimeTypes { get; init; } =
        new[] { "audio/mpeg", "audio/wav", "audio/x-wav", "audio/mp4", "audio/flac", "audio/ogg", "video/mp4", "video/quicktime" };
}

/// <summary>Why a reference was refused. Codes are stable; add, never renumber.</summary>
public enum MusicReferenceEligibilityDenialReason
{
    PolicyLimitsInvalid = 0,
    MissingAuthenticatedPrincipal = 1,
    PrincipalSessionNotVerified = 2,
    PrincipalWorkspaceScopeNotVerified = 3,
    PrincipalWorkspaceScopeDenied = 4,
    MissingReferenceAsset = 5,
    ReferenceMediaTypeUnsupported = 6,
    DuplicateReferenceAsset = 7,
    TooManyReferenceAssets = 8,
    AssetAccessEvidenceMissing = 9,
    AssetAccessEvidenceNotVerified = 10,
    AssetNotFound = 11,
    AssetWorkspaceMismatch = 12,
    AssetAccessDeniedByService = 13,
    MediaProbeEvidenceMissing = 14,
    MediaProbeEvidenceNotVerified = 15,
    MediaContainerUnsupported = 16,
    MediaMimeTypeUnsupported = 17,
    MediaSizeInvalid = 18,
    MediaSizeExceedsLimit = 19,
    MediaDurationUnsupported = 20,
    MediaAudioTrackMissing = 21,
    ReferenceRightsAttestationMissing = 22,
    ReferenceRightsAttestationNotVerified = 23,
    ReferenceRightsAttestationIsUntrustedClientAssertion = 24,
    NamedSingerImitationRequested = 25,
    VoiceRightToUseAuthorizationMissing = 26,
    VoiceRightToUseAuthorizationNotVerified = 27,
    ProviderCapabilityEvidenceMissing = 28,
    ProviderCapabilityNotVerified = 29,
    ProviderReferenceConditioningUnsupported = 30,
    ProviderVideoDerivedReferenceUnsupported = 31,
    ProviderAuthorizedCoverUnsupported = 32,
    ProviderVoiceRightToUseEnforcementUnsupported = 33,
}

/// <summary>One refusal. Messages are user-safe: no URLs, no storage keys, no provider identifiers beyond the configured key.</summary>
public sealed record MusicReferenceEligibilityDenial(
    MusicReferenceEligibilityDenialReason Reason,
    string Code,
    string Message,
    Guid? AssetId = null);

/// <summary>
/// The decision. Ineligible results always carry at least one denial, and eligible results carry
/// none, so a caller can never render "allowed" next with a caveat attached.
/// </summary>
public sealed record MusicReferenceEligibilityResult(
    bool IsEligible,
    IReadOnlyList<MusicReferenceEligibilityDenial> Denials)
{
    public static MusicReferenceEligibilityResult Allow() => new(true, Array.Empty<MusicReferenceEligibilityDenial>());

    internal static readonly IReadOnlyList<MusicReferenceEligibilityDenial> MissingReferenceDenial =
        new[]
        {
            new MusicReferenceEligibilityDenial(
                MusicReferenceEligibilityDenialReason.MissingReferenceAsset,
                "MUSIC_REFERENCE_ASSET_MISSING",
                "Choose the audio or video reference to use.",
                null),
        };

    public static MusicReferenceEligibilityResult Deny(IReadOnlyList<MusicReferenceEligibilityDenial> denials) =>
        new(false, denials is { Count: > 0 } ? denials : MissingReferenceDenial);
}

/// <summary>
/// Provider-free, fail-closed eligibility policy for accepting audio or video reference input to a
/// future Music/Song generation request (issue #708, acceptance criteria 3 and 4). It answers one
/// question only: may this reference be used, on what verified evidence, and if not, why.
///
/// It is a pure decision function over evidence records owned and produced elsewhere. It is not a
/// route, not registered in DI, not called from the generation pipeline, and it makes no provider,
/// billing, storage or network call. Nothing here enables paid Song generation.
///
/// Boundaries with the sibling slices that are already open, restated so the split stays honest:
/// Open PR #709 shapes the request body (creation mode, lyrics, a reference-rights flag, a media type
/// and an asset id). Those are claims; this type validates evidence about the claims and never imports
/// those contracts, so the two cannot disagree by construction.
///
/// Open PR #730 covers reference upload validation and open PR #774 covers reducing a reference video
/// to its audio track. Neither is duplicated: this policy sees no bytes, no upload and no extraction
/// result, only the probed facts recorded about an asset.
///
/// Open PR #359 fences the requesting workspace at the controller; its membership outcome is consumed
/// here as evidence instead of being reimplemented.
///
/// Design commitments: an asset identifier is not an access grant; a checkbox is not a copyright
/// determination and is not treated as one; "verified" needs the service that verified it; a missing
/// record denies rather than defaults to allowed; every applicable reason is reported, not just the
/// first; and a provider that cannot do authorized reference conditioning denies the reference instead
/// of silently generating something else.
/// </summary>
public static class MusicReferenceEligibilityPolicy
{
    public static MusicReferenceEligibilityResult Evaluate(
        MusicReferenceEligibilityRequest? request,
        MusicReferenceEligibilityLimits? limits = null)
    {
        limits ??= new MusicReferenceEligibilityLimits();

        var denials = new List<MusicReferenceEligibilityDenial>();

        if (!LimitsAreSane(limits))
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.PolicyLimitsInvalid));
            return MusicReferenceEligibilityResult.Deny(denials);
        }

        if (request is null)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MissingAuthenticatedPrincipal));
            return MusicReferenceEligibilityResult.Deny(denials);
        }

        EvaluatePrincipal(request.Principal, denials);

        var selectors = request.RequestedAssets ?? Array.Empty<MusicReferenceAssetSelector>();
        EvaluateSelectors(selectors, limits, denials);

        foreach (var selector in selectors)
        {
            EvaluateAssetAccess(selector, request, denials);
            EvaluateMediaProbe(selector, request, limits, denials);
        }

        var mediaTypes = selectors
            .Where(selector => !string.IsNullOrWhiteSpace(selector.MediaType))
            .Select(selector => selector.MediaType!.Trim().ToLowerInvariant())
            .ToArray();

        var requestsVideo = mediaTypes.Contains("video");
        var imitationRequested = request.Intent == MusicReferenceIntent.NamedVoiceImitation
            || !string.IsNullOrWhiteSpace(request.NamedSinger);

        if (selectors.Count > 0)
        {
            EvaluateRights(request, denials);
            EvaluateProvider(request, requestsVideo, imitationRequested, denials);
        }

        return denials.Count == 0
            ? MusicReferenceEligibilityResult.Allow()
            : MusicReferenceEligibilityResult.Deny(denials);
    }

    /// <summary>Stable, machine-readable reason codes for telemetry and client copy mapping.</summary>
    public static string CodeFor(MusicReferenceEligibilityDenialReason reason) => reason switch
    {
        MusicReferenceEligibilityDenialReason.PolicyLimitsInvalid => "MUSIC_REFERENCE_POLICY_MISCONFIGURED",
        MusicReferenceEligibilityDenialReason.MissingAuthenticatedPrincipal => "MUSIC_REFERENCE_PRINCIPAL_MISSING",
        MusicReferenceEligibilityDenialReason.PrincipalSessionNotVerified => "MUSIC_REFERENCE_SESSION_NOT_VERIFIED",
        MusicReferenceEligibilityDenialReason.PrincipalWorkspaceScopeNotVerified => "MUSIC_REFERENCE_WORKSPACE_NOT_VERIFIED",
        MusicReferenceEligibilityDenialReason.PrincipalWorkspaceScopeDenied => "MUSIC_REFERENCE_WORKSPACE_DENIED",
        MusicReferenceEligibilityDenialReason.MissingReferenceAsset => "MUSIC_REFERENCE_ASSET_MISSING",
        MusicReferenceEligibilityDenialReason.ReferenceMediaTypeUnsupported => "MUSIC_REFERENCE_MEDIA_TYPE_UNSUPPORTED",
        MusicReferenceEligibilityDenialReason.DuplicateReferenceAsset => "MUSIC_REFERENCE_ASSET_DUPLICATED",
        MusicReferenceEligibilityDenialReason.TooManyReferenceAssets => "MUSIC_REFERENCE_ASSET_LIMIT_EXCEEDED",
        MusicReferenceEligibilityDenialReason.AssetAccessEvidenceMissing => "MUSIC_REFERENCE_ASSET_ACCESS_UNVERIFIED",
        MusicReferenceEligibilityDenialReason.AssetAccessEvidenceNotVerified => "MUSIC_REFERENCE_ASSET_ACCESS_UNVERIFIED",
        MusicReferenceEligibilityDenialReason.AssetNotFound => "MUSIC_REFERENCE_ASSET_NOT_FOUND",
        MusicReferenceEligibilityDenialReason.AssetWorkspaceMismatch => "MUSIC_REFERENCE_ASSET_WORKSPACE_MISMATCH",
        MusicReferenceEligibilityDenialReason.AssetAccessDeniedByService => "MUSIC_REFERENCE_ASSET_ACCESS_DENIED",
        MusicReferenceEligibilityDenialReason.MediaProbeEvidenceMissing => "MUSIC_REFERENCE_PROBE_MISSING",
        MusicReferenceEligibilityDenialReason.MediaProbeEvidenceNotVerified => "MUSIC_REFERENCE_PROBE_NOT_VERIFIED",
        MusicReferenceEligibilityDenialReason.MediaContainerUnsupported => "MUSIC_REFERENCE_CONTAINER_UNSUPPORTED",
        MusicReferenceEligibilityDenialReason.MediaMimeTypeUnsupported => "MUSIC_REFERENCE_MIME_UNSUPPORTED",
        MusicReferenceEligibilityDenialReason.MediaSizeInvalid => "MUSIC_REFERENCE_SIZE_INVALID",
        MusicReferenceEligibilityDenialReason.MediaSizeExceedsLimit => "MUSIC_REFERENCE_SIZE_EXCEEDS_LIMIT",
        MusicReferenceEligibilityDenialReason.MediaDurationUnsupported => "MUSIC_REFERENCE_DURATION_UNSUPPORTED",
        MusicReferenceEligibilityDenialReason.MediaAudioTrackMissing => "MUSIC_REFERENCE_AUDIO_TRACK_MISSING",
        MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationMissing => "MUSIC_REFERENCE_RIGHTS_MISSING",
        MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationNotVerified => "MUSIC_REFERENCE_RIGHTS_NOT_VERIFIED",
        MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationIsUntrustedClientAssertion => "MUSIC_REFERENCE_RIGHTS_CLIENT_ASSERTION",
        MusicReferenceEligibilityDenialReason.NamedSingerImitationRequested => "MUSIC_REFERENCE_VOICE_IDENTITY_REQUIRED",
        MusicReferenceEligibilityDenialReason.VoiceRightToUseAuthorizationMissing => "MUSIC_REFERENCE_VOICE_RIGHT_MISSING",
        MusicReferenceEligibilityDenialReason.VoiceRightToUseAuthorizationNotVerified => "MUSIC_REFERENCE_VOICE_RIGHT_NOT_VERIFIED",
        MusicReferenceEligibilityDenialReason.ProviderCapabilityEvidenceMissing => "MUSIC_REFERENCE_PROVIDER_CAPABILITY_MISSING",
        MusicReferenceEligibilityDenialReason.ProviderCapabilityNotVerified => "MUSIC_REFERENCE_PROVIDER_CAPABILITY_NOT_VERIFIED",
        MusicReferenceEligibilityDenialReason.ProviderReferenceConditioningUnsupported => "MUSIC_REFERENCE_PROVIDER_UNSUPPORTED",
        MusicReferenceEligibilityDenialReason.ProviderVideoDerivedReferenceUnsupported => "MUSIC_REFERENCE_PROVIDER_VIDEO_UNSUPPORTED",
        MusicReferenceEligibilityDenialReason.ProviderAuthorizedCoverUnsupported => "MUSIC_REFERENCE_PROVIDER_COVER_UNSUPPORTED",
        MusicReferenceEligibilityDenialReason.ProviderVoiceRightToUseEnforcementUnsupported => "MUSIC_REFERENCE_PROVIDER_VOICE_ENFORCEMENT_UNSUPPORTED",
        _ => "MUSIC_REFERENCE_NOT_ELIGIBLE",
    };

    private static void EvaluatePrincipal(
        MusicReferencePrincipalEvidence? principal,
        List<MusicReferenceEligibilityDenial> denials)
    {
        if (principal is null || principal.UserId == Guid.Empty || principal.RequestWorkspaceId == Guid.Empty)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MissingAuthenticatedPrincipal));
            return;
        }

        if (principal.SessionVerification != MusicEvidenceVerificationState.VerifiedByExistingService
            || string.IsNullOrWhiteSpace(principal.VerifiedByService))
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.PrincipalSessionNotVerified));
        }

        switch (principal.WorkspaceMembershipVerification)
        {
            case MusicEvidenceVerificationState.VerifiedDenial:
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.PrincipalWorkspaceScopeDenied));
                break;
            case MusicEvidenceVerificationState.VerifiedByExistingService when !string.IsNullOrWhiteSpace(principal.VerifiedByService):
                break;
            default:
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.PrincipalWorkspaceScopeNotVerified));
                break;
        }
    }

    private static void EvaluateSelectors(
        IReadOnlyList<MusicReferenceAssetSelector> selectors,
        MusicReferenceEligibilityLimits limits,
        List<MusicReferenceEligibilityDenial> denials)
    {
        if (selectors.Count == 0)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MissingReferenceAsset));
            return;
        }

        if (selectors.Count > limits.MaxReferenceAssets)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.TooManyReferenceAssets));
        }

        var seen = new HashSet<Guid>();
        foreach (var selector in selectors)
        {
            if (selector.AssetId == Guid.Empty)
            {
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.MissingReferenceAsset));
                continue;
            }

            if (!seen.Add(selector.AssetId))
            {
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.DuplicateReferenceAsset, selector.AssetId));
            }

            var mediaType = selector.MediaType?.Trim().ToLowerInvariant();
            if (mediaType != "audio" && mediaType != "video")
            {
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.ReferenceMediaTypeUnsupported, selector.AssetId));
            }
        }
    }

    private static void EvaluateAssetAccess(
        MusicReferenceAssetSelector selector,
        MusicReferenceEligibilityRequest request,
        List<MusicReferenceEligibilityDenial> denials)
    {
        if (selector.AssetId == Guid.Empty)
        {
            return;
        }

        var access = (request.AssetAccessEvidence ?? Array.Empty<MusicReferenceAssetAccessEvidence>())
            .FirstOrDefault(item => item.AssetId == selector.AssetId);

        if (access is null)
        {
            // The caller supplied an identifier only. That is exactly the case the issue calls out:
            // the identifier says which asset, nothing about who may read it.
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.AssetAccessEvidenceMissing, selector.AssetId));
            return;
        }

        var serviceNamed = !string.IsNullOrWhiteSpace(access.VerifiedByService);

        switch (access.AssetLookupVerification)
        {
            case MusicEvidenceVerificationState.VerifiedDenial:
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.AssetNotFound, selector.AssetId));
                break;
            case MusicEvidenceVerificationState.VerifiedByExistingService when serviceNamed:
                break;
            default:
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.AssetAccessEvidenceNotVerified, selector.AssetId));
                break;
        }

        switch (access.WorkspaceAccessVerification)
        {
            case MusicEvidenceVerificationState.VerifiedDenial:
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.AssetAccessDeniedByService, selector.AssetId));
                break;
            case MusicEvidenceVerificationState.NotVerified:
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.AssetAccessEvidenceNotVerified, selector.AssetId));
                break;
            case MusicEvidenceVerificationState.VerifiedByExistingService when !serviceNamed:
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.AssetAccessEvidenceNotVerified, selector.AssetId));
                break;
            case MusicEvidenceVerificationState.VerifiedByExistingService:
                // Compare only when the requesting workspace is on record; without a principal the
                // comparison would stack a second, misleading reason on top of the identity denial.
                if (request.Principal is not null
                    && (access.AssetWorkspaceId == Guid.Empty || access.AssetWorkspaceId != request.Principal.RequestWorkspaceId))
                {
                    // A service that confirms access yet reports a different workspace is contradictory.
                    // Contradiction is not agreement, so this denies rather than picking a side.
                    denials.Add(Denial(MusicReferenceEligibilityDenialReason.AssetWorkspaceMismatch, selector.AssetId));
                }

                break;
        }
    }

    private static void EvaluateMediaProbe(
        MusicReferenceAssetSelector selector,
        MusicReferenceEligibilityRequest request,
        MusicReferenceEligibilityLimits limits,
        List<MusicReferenceEligibilityDenial> denials)
    {
        if (selector.AssetId == Guid.Empty)
        {
            return;
        }

        var probe = (request.MediaProbeEvidence ?? Array.Empty<MusicReferenceMediaProbeEvidence>())
            .FirstOrDefault(item => item.AssetId == selector.AssetId);

        if (probe is null)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MediaProbeEvidenceMissing, selector.AssetId));
            return;
        }

        if (probe.ProbeVerification != MusicEvidenceVerificationState.VerifiedByExistingService
            || string.IsNullOrWhiteSpace(probe.VerifiedByService))
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MediaProbeEvidenceNotVerified, selector.AssetId));
            return;
        }

        if (string.IsNullOrWhiteSpace(probe.ContainerName)
            || !limits.SupportedContainers.Contains(probe.ContainerName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MediaContainerUnsupported, selector.AssetId));
        }

        if (string.IsNullOrWhiteSpace(probe.MimeType)
            || !limits.SupportedMimeTypes.Contains(probe.MimeType.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MediaMimeTypeUnsupported, selector.AssetId));
        }

        if (probe.SizeBytes <= 0)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MediaSizeInvalid, selector.AssetId));
        }
        else if (probe.SizeBytes > limits.MaxSizeBytes)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MediaSizeExceedsLimit, selector.AssetId));
        }

        if (double.IsNaN(probe.DurationSeconds) || double.IsInfinity(probe.DurationSeconds)
            || probe.DurationSeconds < limits.MinDurationSeconds || probe.DurationSeconds > limits.MaxDurationSeconds)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MediaDurationUnsupported, selector.AssetId));
        }

        if (!probe.AudioTrackDetected)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.MediaAudioTrackMissing, selector.AssetId));
        }
    }

    private static void EvaluateRights(
        MusicReferenceEligibilityRequest request,
        List<MusicReferenceEligibilityDenial> denials)
    {
        var rights = request.Rights;

        if (rights is null)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationMissing));
        }
        else if (rights.PresentedAsClientCheckboxOnly)
        {
            // Stated plainly: a confirmation control records that a user clicked something. It does
            // not decide who owns the recording or whose voice it is, and it is not accepted here.
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationIsUntrustedClientAssertion));
        }
        else if (rights.ReferenceRightsAttestation != MusicEvidenceVerificationState.VerifiedByExistingService
            || string.IsNullOrWhiteSpace(rights.AttestationRecordId))
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationNotVerified));
        }

        var voiceImitationDeclared = request.Intent == MusicReferenceIntent.NamedVoiceImitation;
        var singerNamed = !string.IsNullOrWhiteSpace(request.NamedSinger);

        if (!voiceImitationDeclared)
        {
            if (singerNamed)
            {
                // A request that names a real singer while declaring a milder intent understates what
                // it is asking for. Refuse it for review instead of quietly reclassifying and moving on.
                denials.Add(Denial(MusicReferenceEligibilityDenialReason.NamedSingerImitationRequested));
            }

            return;
        }

        if (rights is null)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.VoiceRightToUseAuthorizationMissing));
            return;
        }

        if (rights.PresentedAsClientCheckboxOnly)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationIsUntrustedClientAssertion));
        }
        else if (rights.NamedVoiceRightToUse != MusicEvidenceVerificationState.VerifiedByExistingService
            || string.IsNullOrWhiteSpace(rights.RightToUseRecordId))
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.VoiceRightToUseAuthorizationNotVerified));
        }
    }

    private static void EvaluateProvider(
        MusicReferenceEligibilityRequest request,
        bool requestsVideo,
        bool imitationRequested,
        List<MusicReferenceEligibilityDenial> denials)
    {
        var capability = request.ProviderCapability;

        if (capability is null || string.IsNullOrWhiteSpace(capability.ProviderKey))
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ProviderCapabilityEvidenceMissing));
            return;
        }

        if (capability.CapabilityVerification != MusicEvidenceVerificationState.VerifiedByExistingService
            || string.IsNullOrWhiteSpace(capability.VerifiedByService))
        {
            // No verified capability record means nothing was proved about this provider's reference
            // conditioning contract. "Probably fine" is not a basis for a paid, rights-bearing call.
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ProviderCapabilityNotVerified));
            return;
        }

        if (!capability.SupportsAuthorizedAudioReferenceConditioning)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ProviderReferenceConditioningUnsupported));
            return;
        }

        if (requestsVideo && !capability.SupportsVideoDerivedAudioReference)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ProviderVideoDerivedReferenceUnsupported));
        }

        if (request.Intent == MusicReferenceIntent.AuthorizedCoverOrRearrangement
            && !capability.SupportsAuthorizedCoverOrRearrangement)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ProviderAuthorizedCoverUnsupported));
        }

        if (imitationRequested && !capability.SupportsNamedVoiceRightToUseEnforcement)
        {
            denials.Add(Denial(MusicReferenceEligibilityDenialReason.ProviderVoiceRightToUseEnforcementUnsupported));
        }
    }

    private static bool LimitsAreSane(MusicReferenceEligibilityLimits limits) =>
        limits.MaxSizeBytes > 0
        && limits.MinDurationSeconds > 0
        && limits.MaxDurationSeconds >= limits.MinDurationSeconds
        && limits.MaxReferenceAssets > 0
        && limits.SupportedContainers.Count > 0
        && limits.SupportedMimeTypes.Count > 0;

    private static MusicReferenceEligibilityDenial Denial(
        MusicReferenceEligibilityDenialReason reason,
        Guid? assetId = null) => new(reason, CodeFor(reason), MessageFor(reason), assetId);

    private static string MessageFor(MusicReferenceEligibilityDenialReason reason) => reason switch
    {
        MusicReferenceEligibilityDenialReason.PolicyLimitsInvalid =>
            "Reference eligibility cannot be evaluated because its limits are not configured safely.",
        MusicReferenceEligibilityDenialReason.MissingAuthenticatedPrincipal =>
            "Sign in again, then retry this reference request.",
        MusicReferenceEligibilityDenialReason.PrincipalSessionNotVerified =>
            "The signed-in session for this request could not be confirmed.",
        MusicReferenceEligibilityDenialReason.PrincipalWorkspaceScopeNotVerified =>
            "Workspace membership for this request could not be confirmed.",
        MusicReferenceEligibilityDenialReason.PrincipalWorkspaceScopeDenied =>
            "You are not a member of the workspace used for this request.",
        MusicReferenceEligibilityDenialReason.MissingReferenceAsset =>
            "Choose the audio or video reference to use.",
        MusicReferenceEligibilityDenialReason.ReferenceMediaTypeUnsupported =>
            "A reference must be selected as either audio or video.",
        MusicReferenceEligibilityDenialReason.DuplicateReferenceAsset =>
            "The same reference was selected more than once.",
        MusicReferenceEligibilityDenialReason.TooManyReferenceAssets =>
            "Too many references were selected for one request.",
        MusicReferenceEligibilityDenialReason.AssetAccessEvidenceMissing =>
            "The selected reference could not be confirmed as an asset you may use.",
        MusicReferenceEligibilityDenialReason.AssetAccessEvidenceNotVerified =>
            "Access to the selected reference has not been confirmed.",
        MusicReferenceEligibilityDenialReason.AssetNotFound =>
            "The selected reference is no longer available.",
        MusicReferenceEligibilityDenialReason.AssetWorkspaceMismatch =>
            "The selected reference belongs to a different workspace.",
        MusicReferenceEligibilityDenialReason.AssetAccessDeniedByService =>
            "Access to the selected reference was refused.",
        MusicReferenceEligibilityDenialReason.MediaProbeEvidenceMissing =>
            "The selected reference has not been inspected yet. Try again in a moment.",
        MusicReferenceEligibilityDenialReason.MediaProbeEvidenceNotVerified =>
            "The inspection result for the selected reference could not be confirmed.",
        MusicReferenceEligibilityDenialReason.MediaContainerUnsupported =>
            "That reference file format is not supported.",
        MusicReferenceEligibilityDenialReason.MediaMimeTypeUnsupported =>
            "The declared type of the selected reference does not match a supported format.",
        MusicReferenceEligibilityDenialReason.MediaSizeInvalid =>
            "The selected reference is empty or its size is unknown.",
        MusicReferenceEligibilityDenialReason.MediaSizeExceedsLimit =>
            "The selected reference is larger than the allowed size.",
        MusicReferenceEligibilityDenialReason.MediaDurationUnsupported =>
            "The selected reference is outside the supported length range.",
        MusicReferenceEligibilityDenialReason.MediaAudioTrackMissing =>
            "The selected reference contains no audio to reference.",
        MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationMissing =>
            "Reference rights have not been recorded for this request.",
        MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationNotVerified =>
            "The recorded reference rights could not be confirmed.",
        MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationIsUntrustedClientAssertion =>
            "A confirmation in the form is not enough; reference rights must be recorded before use.",
        MusicReferenceEligibilityDenialReason.NamedSingerImitationRequested =>
            "Imitating an identifiable singer's voice requires separate authorization.",
        MusicReferenceEligibilityDenialReason.VoiceRightToUseAuthorizationMissing =>
            "No right-to-use authorization exists for the named voice.",
        MusicReferenceEligibilityDenialReason.VoiceRightToUseAuthorizationNotVerified =>
            "The right-to-use authorization for the named voice could not be confirmed.",
        MusicReferenceEligibilityDenialReason.ProviderCapabilityEvidenceMissing =>
            "Music reference generation is unavailable right now.",
        MusicReferenceEligibilityDenialReason.ProviderCapabilityNotVerified =>
            "Music reference generation is not verified as available yet.",
        MusicReferenceEligibilityDenialReason.ProviderReferenceConditioningUnsupported =>
            "The current music provider cannot condition generation on an authorized audio reference.",
        MusicReferenceEligibilityDenialReason.ProviderVideoDerivedReferenceUnsupported =>
            "The current music provider cannot use audio extracted from a reference video.",
        MusicReferenceEligibilityDenialReason.ProviderAuthorizedCoverUnsupported =>
            "The current music provider cannot produce an authorized cover or rearrangement.",
        MusicReferenceEligibilityDenialReason.ProviderVoiceRightToUseEnforcementUnsupported =>
            "The current music provider cannot enforce voice right-to-use limits.",
        _ => "This reference cannot be used for music generation.",
    };
}
