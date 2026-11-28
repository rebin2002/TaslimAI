using Taslim.Api.Domain;
namespace Taslim.Api.Music;

/// <summary>
/// Server-side boundary that answers one user-facing question for Music Studio (issue #708,
/// acceptance criteria 3 and 4): <em>may this existing private workspace asset be used as a song
/// reference, and if not, exactly why?</em> It is the wiring that the two unowned halves of the
/// slice were each waiting for:
/// <list type="bullet">
/// <item><see cref="MusicReferenceAssetAuthorizer"/> (asset/storage authorization path) turns bare
/// asset identifiers into verified access records or explicit denials, and deliberately registers
/// itself nowhere.</item>
/// <item><see cref="MusicReferenceEligibilityPolicy"/> is a pure fail-closed decision function whose
/// <see cref="MusicReferenceAssetAccessEvidence"/> and <see cref="MusicReferenceMediaProbeEvidence"/>
/// records are documented as "supplied by" other paths.</item>
/// </list>
/// This boundary is the producer/consumer join: it asks the authorizer, maps the verified outcome
/// onto the policy's evidence records field-for-field, and returns the decision with stable
/// fail-closed reason codes. It adds no new decision rules of its own.
///
/// What it is NOT: not an upload endpoint, not a media probe, not a provider adapter, not a
/// generation admission gate. It reads no file bytes, never extracts audio, never names a provider
/// capability as available, never creates a job, never touches billing, cost controls, credits or
/// any paid call, and never grants permission to generate. A reference reported eligible here is
/// still refused by the generation path until a rights record and a verified provider contract
/// exist; a reference refused here is simply not usable later.
///
/// Privacy invariants inherited from the two joined components and asserted by the HTTP tests:
/// only the requesting, authenticated workspace member can resolve an asset at all; the response
/// carries server-recorded facts about that asset (media kind, size, recorded duration, container)
/// and stable reason codes, and never storage keys, file names, paths, URLs, provider details or
/// any content-derived text.
/// </summary>
public sealed record MusicReferenceBoundaryBlocker(string Code, string Message, Guid? AssetId);

/// <summary>
/// The server-recorded facts a client may display for one reference that passed asset
/// authorization. Nulls mean "the server has never recorded this", never "zero" and never a guess:
/// the duration of a video, for example, is only established by the inspection step that owns it.
/// </summary>
public sealed record MusicReferenceBoundaryVerifiedFact(
    Guid AssetId,
    string MediaType,
    string ContentType,
    string? ContainerFormat,
    long SizeBytes,
    double? DurationSeconds,
    bool? AudioTrackDetected);

/// <summary>The complete, displayable answer for one reference-selection request.</summary>
public sealed record MusicReferenceBoundaryDecision(
    bool IsEligible,
    Guid WorkspaceId,
    IReadOnlyList<MusicReferenceBoundaryVerifiedFact> VerifiedFacts,
    IReadOnlyList<MusicReferenceBoundaryBlocker> Blockers);

/// <summary>One requested reference as the client declared it: a kind claim plus an asset address.</summary>
public sealed record MusicReferenceBoundarySelection(string? MediaType, Guid AssetId);

/// <summary>
/// Workspace-scoped, read-only evidence join for Music Studio reference selection. Registered in
/// the service collection so the boundary cannot be reached without authentication; it owns no
/// storage, provider, billing or generation dependency.
/// </summary>
public interface IMusicReferenceEligibilityBoundary
{
    Task<MusicReferenceBoundaryDecision> EvaluateAsync(
        Guid userId,
        Guid workspaceId,
        IReadOnlyList<MusicReferenceBoundarySelection?> selections,
        MusicReferenceIntent intent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default boundary. Every fact it reports is either produced by the asset authorizer from server
/// rows or refused with a code from the policy; nothing is inferred from the client's declaration
/// beyond the media kind it claims, which the authorizer must confirm.
/// </summary>
public sealed class MusicReferenceEligibilityBoundary(
    IMusicReferenceAssetAuthorizer authorizer) : IMusicReferenceEligibilityBoundary
{
    /// <summary>
    /// Sentinel handed to the policy when the server has never recorded a duration. The policy's
    /// probe evidence is a non-nullable field, so the alternative would be inventing a number: a
    /// fabricated 0.0 would claim a measured empty asset, while any positive value would launder an
    /// unmeasured asset into an "inspected" one. The sentinel is denied by the policy as
    /// <c>MUSIC_REFERENCE_DURATION_UNSUPPORTED</c>, which is the honest "absent audio metadata"
    /// answer; a real probe step (issue #708 criterion 3) is what replaces it.
    /// </summary>
    internal const double DurationNotOnRecord = -1d;

    public async Task<MusicReferenceBoundaryDecision> EvaluateAsync(
        Guid userId,
        Guid workspaceId,
        IReadOnlyList<MusicReferenceBoundarySelection?> selections,
        MusicReferenceIntent intent,
        CancellationToken cancellationToken = default)
    {
        // The selection list is forwarded whole: the lookup ceiling is the authorizer's own rule and
        // it refuses an oversized request before performing any lookup, so truncating here would both
        // hide that refusal and answer a different question than the one the user asked.
        var requested = (selections ?? Array.Empty<MusicReferenceBoundarySelection?>()).ToList();

        var authorization = await authorizer.AuthorizeAsync(
            userId,
            workspaceId,
            requested
                .Select(selection => selection is null
                    ? null
                    : new MusicReferenceAssetSelection(selection.MediaType, selection.AssetId))
                .ToList(),
            cancellationToken);

        var blockers = new List<MusicReferenceBoundaryBlocker>();

        // Authorization refusals are reported first, in the authorizer's own vocabulary. They answer
        // "is this even yours to use", which is a different question from the policy's "is this
        // usable as a reference", and the two code namespaces stay side by side by design.
        foreach (var denial in authorization.Denials)
        {
            blockers.Add(new MusicReferenceBoundaryBlocker(denial.Code, denial.Message, null));
        }

        var verifiedFacts = new List<MusicReferenceBoundaryVerifiedFact>();
        var accessEvidence = new List<MusicReferenceAssetAccessEvidence>();
        var probeEvidence = new List<MusicReferenceMediaProbeEvidence>();

        foreach (var asset in authorization.Assets)
        {
            verifiedFacts.Add(new MusicReferenceBoundaryVerifiedFact(
                asset.AssetId,
                asset.RequestedMediaType,
                asset.ContentType,
                asset.ContainerFormat,
                asset.SizeBytes,
                asset.DurationSeconds,
                asset.AudioTrackDetected));

            accessEvidence.Add(new MusicReferenceAssetAccessEvidence(
                asset.AssetId,
                asset.WorkspaceId,
                MusicEvidenceVerificationState.VerifiedByExistingService,
                MusicEvidenceVerificationState.VerifiedByExistingService,
                asset.VerifiedByService));

            probeEvidence.Add(new MusicReferenceMediaProbeEvidence(
                asset.AssetId,
                asset.ContainerFormat,
                asset.ContentType,
                asset.SizeBytes,
                asset.DurationSeconds ?? DurationNotOnRecord,
                asset.AudioTrackDetected ?? false,
                MusicEvidenceVerificationState.VerifiedByExistingService,
                asset.VerifiedByService));
        }

        var selectors = authorization.Assets
            .Select(asset => new MusicReferenceAssetSelector(asset.RequestedMediaType, asset.AssetId))
            .ToList();

        // The eligibility layers (media facts, rights, provider contract) all describe an asset the
        // requester may actually read. When every selection was refused at the access layer there is
        // nothing left to judge, so the boundary returns the access refusals alone instead of
        // inventing a synthetic "no reference selected" reason on top of them.
        if (selectors.Count == 0)
        {
            return new MusicReferenceBoundaryDecision(false, workspaceId, verifiedFacts, blockers);
        }

        // Rights and provider capability are reported exactly as they stand: no attestation service
        // and no verified reference-conditioning contract exist on this codebase, so the only honest
        // evidence is "not verified". Fabricating either would turn this boundary into a promise the
        // product cannot keep, which is precisely what issue #708 warns against.
        var result = MusicReferenceEligibilityPolicy.Evaluate(new MusicReferenceEligibilityRequest(
            intent,
            // The route is authentication-gated, so the session fact is issued by the pipeline's
            // own authentication service; workspace membership is confirmed by the boundary below
            // before this method is ever reached. Neither claim is a client assertion.
            new MusicReferencePrincipalEvidence(
                userId,
                workspaceId,
                MusicEvidenceVerificationState.VerifiedByExistingService,
                MusicEvidenceVerificationState.VerifiedByExistingService,
                MusicReferenceAssetAuthorizationCodes.VerifiedByService),
            selectors,
            accessEvidence,
            probeEvidence,
            Rights: null,
            ProviderCapability: null,
            // No caller can name a singer through this boundary: there is no right-to-use record
            // service to verify against, so the only honest value is the absence of a name. An
            // imitation intent is still refused below by the policy's voice right-to-use rules.
            NamedSinger: null));

        foreach (var denial in result.Denials)
        {
            blockers.Add(new MusicReferenceBoundaryBlocker(denial.Code, denial.Message, denial.AssetId));
        }

        // The decision is the policy's, never this boundary's: any authorization refusal or any
        // policy denial means ineligible, and this boundary never upgrades a refusal into an allow
        // of its own.
        return new MusicReferenceBoundaryDecision(
            result.IsEligible && blockers.Count == 0,
            workspaceId,
            verifiedFacts,
            blockers);
    }
}
