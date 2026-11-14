using Taslim.Api.Music;
using Xunit;

namespace Taslim.Api.Tests;

// Deterministic unit coverage for the #708 criterion-3/4 reference-eligibility contract.
// Pure decision logic only: no web factory, database, provider, billing, storage, filesystem or
// network dependency exists in any test here, which is also the proof that the policy is
// provider-free and cannot reach anything outside its inputs.
public sealed class MusicReferenceEligibilityPolicyTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ForeignWorkspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AssetId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void Authorized_audio_reference_with_service_verified_evidence_is_accepted()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request());

        Assert.True(result.IsEligible);
        Assert.Empty(result.Denials);
    }

    [Fact]
    public void Video_reference_is_accepted_only_when_video_derived_conditioning_is_verified()
    {
        var eligible = MusicReferenceEligibilityPolicy.Evaluate(Request(
            assets: new[] { new MusicReferenceAssetSelector("video", AssetId) },
            probes: new[] { VerifiedProbe("mp4", "video/mp4") }));

        Assert.True(eligible.IsEligible);

        var videoBlind = MusicReferenceEligibilityPolicy.Evaluate(Request(
            assets: new[] { new MusicReferenceAssetSelector("video", AssetId) },
            probes: new[] { VerifiedProbe("mov", "video/quicktime") },
            capability: new MusicProviderReferenceCapabilityEvidence(
                "provider-a",
                MusicEvidenceVerificationState.VerifiedByExistingService,
                SupportsAuthorizedAudioReferenceConditioning: true,
                SupportsVideoDerivedAudioReference: false,
                VerifiedByService: "capability-registry")));

        Assert.False(videoBlind.IsEligible);
        AssertReason(videoBlind, MusicReferenceEligibilityDenialReason.ProviderVideoDerivedReferenceUnsupported);
    }

    [Fact]
    public void Cross_workspace_asset_is_denied_even_when_a_service_verified_the_lookup()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(
            access: new[] { ForeignAssetAccess(ForeignWorkspaceId) }));

        Assert.False(result.IsEligible);
        AssertReason(result, MusicReferenceEligibilityDenialReason.AssetWorkspaceMismatch);
        Assert.Equal("MUSIC_REFERENCE_ASSET_WORKSPACE_MISMATCH", result.Denials[0].Code);
    }

    [Fact]
    public void Access_denied_by_a_service_is_reported_as_a_denial_not_as_missing_evidence()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(
            access: new[]
            {
                new MusicReferenceAssetAccessEvidence(
                    AssetId,
                    WorkspaceId,
                    MusicEvidenceVerificationState.VerifiedByExistingService,
                    MusicEvidenceVerificationState.VerifiedDenial,
                    "asset-authorization"),
            }));

        Assert.False(result.IsEligible);
        AssertReason(result, MusicReferenceEligibilityDenialReason.AssetAccessDeniedByService);
    }

    [Fact]
    public void Asset_identifier_alone_is_not_access_evidence()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(access: Array.Empty<MusicReferenceAssetAccessEvidence>()));

        Assert.False(result.IsEligible);
        AssertReason(result, MusicReferenceEligibilityDenialReason.AssetAccessEvidenceMissing);
    }

    [Fact]
    public void Claimed_verification_without_a_naming_service_is_not_verification()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(
            access: new[]
            {
                new MusicReferenceAssetAccessEvidence(
                    AssetId,
                    WorkspaceId,
                    MusicEvidenceVerificationState.VerifiedByExistingService,
                    MusicEvidenceVerificationState.VerifiedByExistingService,
                    VerifiedByService: null),
            }));

        Assert.False(result.IsEligible);
        AssertReason(result, MusicReferenceEligibilityDenialReason.AssetAccessEvidenceNotVerified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_unverified_identity_and_scope_denies(string? verifiedByService)
    {
        var principal = new MusicReferencePrincipalEvidence(
            UserId,
            WorkspaceId,
            MusicEvidenceVerificationState.VerifiedByExistingService,
            MusicEvidenceVerificationState.VerifiedByExistingService,
            verifiedByService);

        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(principal: principal));

        // Whitespace is not a service name: an unnamed verifier cannot vouch for anything.
        Assert.False(result.IsEligible);
        AssertReason(result, MusicReferenceEligibilityDenialReason.PrincipalSessionNotVerified);
        AssertReason(result, MusicReferenceEligibilityDenialReason.PrincipalWorkspaceScopeNotVerified);
    }

    [Fact]
    public void Absent_principal_denies_before_any_asset_or_rights_reason_is_considered()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(omitPrincipal: true));

        Assert.False(result.IsEligible);
        AssertReason(result, MusicReferenceEligibilityDenialReason.MissingAuthenticatedPrincipal);
        Assert.Single(result.Denials);
    }

    [Fact]
    public void Membership_refused_by_the_scope_service_is_distinct_from_membership_never_checked()
    {
        var refused = MusicReferenceEligibilityPolicy.Evaluate(Request(principal: new MusicReferencePrincipalEvidence(
            UserId,
            WorkspaceId,
            MusicEvidenceVerificationState.VerifiedByExistingService,
            MusicEvidenceVerificationState.VerifiedDenial,
            "session-service")));

        Assert.False(refused.IsEligible);
        AssertReason(refused, MusicReferenceEligibilityDenialReason.PrincipalWorkspaceScopeDenied);

        var neverChecked = MusicReferenceEligibilityPolicy.Evaluate(Request(principal: new MusicReferencePrincipalEvidence(
            UserId,
            WorkspaceId,
            MusicEvidenceVerificationState.VerifiedByExistingService,
            MusicEvidenceVerificationState.NotVerified,
            "session-service")));

        Assert.False(neverChecked.IsEligible);
        AssertReason(neverChecked, MusicReferenceEligibilityDenialReason.PrincipalWorkspaceScopeNotVerified);
    }

    [Fact]
    public void Uninspected_reference_denies_rather_than_trusting_the_client_file_name()
    {
        AssertReason(
            MusicReferenceEligibilityPolicy.Evaluate(Request(probes: Array.Empty<MusicReferenceMediaProbeEvidence>())),
            MusicReferenceEligibilityDenialReason.MediaProbeEvidenceMissing);

        AssertReason(
            MusicReferenceEligibilityPolicy.Evaluate(Request(probes: new[]
            {
                new MusicReferenceMediaProbeEvidence(AssetId, "mp3", "audio/mpeg", 4_000_000, 120, true),
            })),
            MusicReferenceEligibilityDenialReason.MediaProbeEvidenceNotVerified);
    }

    [Theory]
    [InlineData("mkv", "audio/mpeg", 4_000_000L, 120, true, MusicReferenceEligibilityDenialReason.MediaContainerUnsupported)]
    [InlineData("mp3", "video/avi", 4_000_000L, 120, true, MusicReferenceEligibilityDenialReason.MediaMimeTypeUnsupported)]
    [InlineData("mp3", "audio/mpeg", 0L, 120, true, MusicReferenceEligibilityDenialReason.MediaSizeInvalid)]
    [InlineData("mp3", "audio/mpeg", 26L * 1_048_576, 120, true, MusicReferenceEligibilityDenialReason.MediaSizeExceedsLimit)]
    [InlineData("mp3", "audio/mpeg", 4_000_000L, 4, true, MusicReferenceEligibilityDenialReason.MediaDurationUnsupported)]
    [InlineData("mp3", "audio/mpeg", 4_000_000L, 3601, true, MusicReferenceEligibilityDenialReason.MediaDurationUnsupported)]
    [InlineData("mp4", "video/mp4", 4_000_000L, 120, false, MusicReferenceEligibilityDenialReason.MediaAudioTrackMissing)]
    public void Unsupported_format_size_or_duration_denies(
        string container,
        string mimeType,
        long sizeBytes,
        double durationSeconds,
        bool audioTrackDetected,
        MusicReferenceEligibilityDenialReason expected)
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(probes: new[]
        {
            new MusicReferenceMediaProbeEvidence(
                AssetId, container, mimeType, sizeBytes, durationSeconds, audioTrackDetected,
                MusicEvidenceVerificationState.VerifiedByExistingService, "media-inspector"),
        }));

        Assert.False(result.IsEligible);
        AssertReason(result, expected);
    }

    [Fact]
    public void Missing_rights_attestation_denies_and_a_client_checkbox_is_never_attestation()
    {
        var absent = MusicReferenceEligibilityPolicy.Evaluate(Request(omitRights: true));

        Assert.False(absent.IsEligible);
        AssertReason(absent, MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationMissing);

        // The exact shape of issue #708 criterion 3/4: the UI ticked a box, nothing else vouches for it.
        var checkbox = MusicReferenceEligibilityPolicy.Evaluate(Request(rights: new MusicReferenceRightsEvidence(
            MusicEvidenceVerificationState.VerifiedByExistingService,
            "attestation-1",
            PresentedAsClientCheckboxOnly: true)));

        Assert.False(checkbox.IsEligible);
        AssertReason(checkbox, MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationIsUntrustedClientAssertion);

        var unrecorded = MusicReferenceEligibilityPolicy.Evaluate(Request(rights: new MusicReferenceRightsEvidence(
            MusicEvidenceVerificationState.VerifiedByExistingService,
            AttestationRecordId: null)));

        Assert.False(unrecorded.IsEligible);
        AssertReason(unrecorded, MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationNotVerified);
    }

    [Fact]
    public void Imitating_an_identifiable_singer_requires_verified_right_to_use_authorization()
    {
        var unauthorized = MusicReferenceEligibilityPolicy.Evaluate(Request(
            intent: MusicReferenceIntent.NamedVoiceImitation,
            namedSinger: "a recording artist"));

        Assert.False(unauthorized.IsEligible);
        AssertReason(unauthorized, MusicReferenceEligibilityDenialReason.VoiceRightToUseAuthorizationNotVerified);

        var authorized = MusicReferenceEligibilityPolicy.Evaluate(Request(
            intent: MusicReferenceIntent.NamedVoiceImitation,
            namedSinger: "a recording artist",
            rights: new MusicReferenceRightsEvidence(
                MusicEvidenceVerificationState.VerifiedByExistingService,
                "attestation-1",
                MusicEvidenceVerificationState.VerifiedByExistingService,
                "right-to-use-77")));

        Assert.True(authorized.IsEligible);
        Assert.Empty(authorized.Denials);
    }

    [Fact]
    public void Imitation_denies_when_the_provider_cannot_enforce_the_recorded_right_to_use()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(
            intent: MusicReferenceIntent.NamedVoiceImitation,
            namedSinger: "a recording artist",
            rights: new MusicReferenceRightsEvidence(
                MusicEvidenceVerificationState.VerifiedByExistingService,
                "attestation-1",
                MusicEvidenceVerificationState.VerifiedByExistingService,
                "right-to-use-77"),
            capability: new MusicProviderReferenceCapabilityEvidence(
                "provider-a",
                MusicEvidenceVerificationState.VerifiedByExistingService,
                SupportsAuthorizedAudioReferenceConditioning: true,
                SupportsVideoDerivedAudioReference: true,
                SupportsAuthorizedCoverOrRearrangement: true,
                SupportsNamedVoiceRightToUseEnforcement: false,
                VerifiedByService: "capability-registry")));

        Assert.False(result.IsEligible);
        AssertReason(result, MusicReferenceEligibilityDenialReason.ProviderVoiceRightToUseEnforcementUnsupported);
    }

    [Fact]
    public void Naming_a_singer_outside_the_imitation_intent_is_refused_for_review_not_reclassified()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(
            intent: MusicReferenceIntent.StyleConditioning,
            namedSinger: "a recording artist"));

        Assert.False(result.IsEligible);
        AssertReason(result, MusicReferenceEligibilityDenialReason.NamedSingerImitationRequested);
    }

    [Theory]
    [InlineData("mubert", false)]
    [InlineData("stable-audio", false)]
    [InlineData("provider-a", true)]
    public void Instrumental_only_providers_fail_closed_instead_of_silently_dropping_the_reference(
        string providerKey,
        bool supportsReferenceConditioning)
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(capability:
            new MusicProviderReferenceCapabilityEvidence(
                providerKey,
                MusicEvidenceVerificationState.VerifiedByExistingService,
                SupportsAuthorizedAudioReferenceConditioning: supportsReferenceConditioning,
                VerifiedByService: "capability-registry")));

        Assert.Equal(supportsReferenceConditioning, result.IsEligible);

        if (!supportsReferenceConditioning)
        {
            AssertReason(result, MusicReferenceEligibilityDenialReason.ProviderReferenceConditioningUnsupported);
        }
    }

    [Fact]
    public void Unverified_or_absent_provider_capability_denies()
    {
        AssertReason(
            MusicReferenceEligibilityPolicy.Evaluate(Request(omitCapability: true)),
            MusicReferenceEligibilityDenialReason.ProviderCapabilityEvidenceMissing);

        // A provider identifier that was never verified against its documented contract proves nothing.
        AssertReason(
            MusicReferenceEligibilityPolicy.Evaluate(Request(capability: new MusicProviderReferenceCapabilityEvidence("suno"))),
            MusicReferenceEligibilityDenialReason.ProviderCapabilityNotVerified);
    }

    [Fact]
    public void Selector_shape_violations_are_denied()
    {
        var noAsset = MusicReferenceEligibilityPolicy.Evaluate(Request(assets: Array.Empty<MusicReferenceAssetSelector>()));

        Assert.False(noAsset.IsEligible);
        AssertReason(noAsset, MusicReferenceEligibilityDenialReason.MissingReferenceAsset);

        var externalUrl = MusicReferenceEligibilityPolicy.Evaluate(Request(
            assets: new[] { new MusicReferenceAssetSelector("url", AssetId) },
            probes: new[] { VerifiedProbe("mp3", "audio/mpeg") }));

        AssertReason(externalUrl, MusicReferenceEligibilityDenialReason.ReferenceMediaTypeUnsupported);

        var duplicated = MusicReferenceEligibilityPolicy.Evaluate(Request(
            assets: new[]
            {
                new MusicReferenceAssetSelector("audio", AssetId),
                new MusicReferenceAssetSelector("audio", AssetId),
            },
            probes: new[] { VerifiedProbe() }));

        AssertReason(duplicated, MusicReferenceEligibilityDenialReason.DuplicateReferenceAsset);
    }

    [Fact]
    public void Every_applicable_reason_is_reported_not_only_the_first()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(
            access: Array.Empty<MusicReferenceAssetAccessEvidence>(),
            probes: Array.Empty<MusicReferenceMediaProbeEvidence>(),
            omitPrincipal: true,
            omitRights: true,
            omitCapability: true));

        Assert.False(result.IsEligible);
        Assert.Contains(result.Denials, item => item.Reason == MusicReferenceEligibilityDenialReason.MissingAuthenticatedPrincipal);
        Assert.Contains(result.Denials, item => item.Reason == MusicReferenceEligibilityDenialReason.AssetAccessEvidenceMissing);
        Assert.Contains(result.Denials, item => item.Reason == MusicReferenceEligibilityDenialReason.MediaProbeEvidenceMissing);
        Assert.Contains(result.Denials, item => item.Reason == MusicReferenceEligibilityDenialReason.ReferenceRightsAttestationMissing);
        Assert.Contains(result.Denials, item => item.Reason == MusicReferenceEligibilityDenialReason.ProviderCapabilityEvidenceMissing);
    }

    [Fact]
    public void Defaulted_evidence_is_never_sufficient()
    {
        Assert.Equal(MusicEvidenceVerificationState.NotVerified, default);

        // Records built with no verification argument at all must deny, so an adapter that forgets to
        // populate evidence cannot produce an accidental allow.
        var bare = MusicReferenceEligibilityPolicy.Evaluate(Request(
            principal: new MusicReferencePrincipalEvidence(UserId, WorkspaceId),
            access: new[] { new MusicReferenceAssetAccessEvidence(AssetId, WorkspaceId) },
            probes: new[] { new MusicReferenceMediaProbeEvidence(AssetId, "mp3", "audio/mpeg", 4_000_000, 120, true) },
            rights: new MusicReferenceRightsEvidence(),
            capability: new MusicProviderReferenceCapabilityEvidence("provider-a")));

        Assert.False(bare.IsEligible);
    }

    [Fact]
    public void Degenerate_limits_configuration_denies_instead_of_allowing()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(
            Request(),
            new MusicReferenceEligibilityLimits { MaxSizeBytes = 0 });

        Assert.False(result.IsEligible);
        AssertReason(result, MusicReferenceEligibilityDenialReason.PolicyLimitsInvalid);
    }

    [Fact]
    public void Denial_codes_are_stable_and_messages_never_carry_urls_or_identifiers()
    {
        var result = MusicReferenceEligibilityPolicy.Evaluate(Request(
            access: new[] { ForeignAssetAccess(ForeignWorkspaceId) }));

        Assert.False(result.IsEligible);
        Assert.Equal("MUSIC_REFERENCE_ASSET_WORKSPACE_MISMATCH", result.Denials[0].Code);

        foreach (var denial in result.Denials)
        {
            Assert.StartsWith("MUSIC_REFERENCE", denial.Code, StringComparison.Ordinal);
            Assert.DoesNotContain("http", denial.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AssetId.ToString(), denial.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(denial.Message));
        }
    }

    private static MusicReferenceEligibilityRequest Request(
        MusicReferenceIntent intent = MusicReferenceIntent.StyleConditioning,
        MusicReferencePrincipalEvidence? principal = null,
        IReadOnlyList<MusicReferenceAssetSelector>? assets = null,
        IReadOnlyList<MusicReferenceAssetAccessEvidence>? access = null,
        IReadOnlyList<MusicReferenceMediaProbeEvidence>? probes = null,
        MusicReferenceRightsEvidence? rights = null,
        MusicProviderReferenceCapabilityEvidence? capability = null,
        string? namedSinger = null,
        bool omitPrincipal = false,
        bool omitRights = false,
        bool omitCapability = false) => new(
            intent,
            principal ?? (omitPrincipal ? null : VerifiedPrincipal()),
            assets ?? new[] { new MusicReferenceAssetSelector("audio", AssetId) },
            access ?? new[] { new MusicReferenceAssetAccessEvidence(
                AssetId,
                WorkspaceId,
                MusicEvidenceVerificationState.VerifiedByExistingService,
                MusicEvidenceVerificationState.VerifiedByExistingService,
                "asset-authorization") },
            probes ?? new[] { VerifiedProbe() },
            rights ?? (omitRights ? null : VerifiedRights()),
            capability ?? (omitCapability ? null : VerifiedCapability()),
            namedSinger);

    // A fully evidenced baseline. The omit* flags above are the only way to remove a record, so a
    // test that means "no evidence at all" cannot silently receive this one.
    private static MusicReferencePrincipalEvidence VerifiedPrincipal() => new(
        UserId,
        WorkspaceId,
        MusicEvidenceVerificationState.VerifiedByExistingService,
        MusicEvidenceVerificationState.VerifiedByExistingService,
        "session-service");

    private static MusicReferenceRightsEvidence VerifiedRights(
        MusicEvidenceVerificationState voiceRightToUse = MusicEvidenceVerificationState.NotVerified) => new(
            MusicEvidenceVerificationState.VerifiedByExistingService,
            "attestation-1",
            voiceRightToUse,
            voiceRightToUse == MusicEvidenceVerificationState.VerifiedByExistingService ? "right-to-use-77" : null);

    private static MusicProviderReferenceCapabilityEvidence VerifiedCapability() => new(
        "provider-a",
        MusicEvidenceVerificationState.VerifiedByExistingService,
        SupportsAuthorizedAudioReferenceConditioning: true,
        SupportsVideoDerivedAudioReference: true,
        SupportsAuthorizedCoverOrRearrangement: true,
        SupportsNamedVoiceRightToUseEnforcement: true,
        VerifiedByService: "capability-registry");

    private static MusicReferenceMediaProbeEvidence VerifiedProbe(
        string container = "mp3",
        string mimeType = "audio/mpeg",
        long sizeBytes = 4_000_000,
        double durationSeconds = 120) => new(
            AssetId,
            container,
            mimeType,
            sizeBytes,
            durationSeconds,
            true,
            MusicEvidenceVerificationState.VerifiedByExistingService,
            "media-inspector");

    private static MusicReferenceAssetAccessEvidence ForeignAssetAccess(Guid assetWorkspaceId) => new(
        AssetId,
        assetWorkspaceId,
        MusicEvidenceVerificationState.VerifiedByExistingService,
        MusicEvidenceVerificationState.VerifiedByExistingService,
        "asset-authorization");

    private static void AssertReason(MusicReferenceEligibilityResult result, MusicReferenceEligibilityDenialReason expected) =>
        Assert.Contains(result.Denials, denial => denial.Reason == expected);
}
