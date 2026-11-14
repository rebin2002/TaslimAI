using Taslim.Api.Assets;
using Taslim.Api.Domain;

namespace Taslim.Api.Music;

/// <summary>
/// Server-side asset authorization path for Music Studio reference media (issue #708,
/// acceptance criterion 3: "the backend must associate an authorized workspace Asset ID and
/// the server must validate access"). This is the step the open song/reference slices all
/// assume but none of them provide:
/// <list type="bullet">
/// <item>PR #709's <c>MusicGenerationCapabilityCatalog</c> keeps
/// <c>SupportsAuthorizedWorkspaceReferences</c> false "only after the request path resolves each
/// asset ID inside its workspace", and its validator refuses reference requests with
/// <c>MUSIC_PROVIDER_UNAVAILABLE</c> while that flag is false. This service is that request path:
/// it turns a bare asset identifier into a verified access record, or into an explicit denial.</item>
/// <item>PR #776's <c>MusicReferenceEligibilityPolicy</c> is a pure decision function over
/// evidence "owned and produced elsewhere", and its <c>MusicReferenceAssetAccessEvidence</c>
/// doc says the record is "supplied by the asset/storage authorization path". Each
/// <see cref="MusicReferenceAuthorizedAsset"/> produced here maps one-to-one onto that access
/// evidence record (asset id, asset workspace, verification state, naming service), and its
/// media fields map onto <c>MusicReferenceMediaProbeEvidence</c> for the facts the server already
/// recorded during file processing. When #776 lands, a thin adapter can fill both evidence
/// records from this outcome without either file importing the other's types.</item>
/// </list>
/// The authorizer performs no eligibility judgement of its own: it never decides whether a MIME
/// type, size, or duration is acceptable for generation (that is #776's policy and #709's
/// validator), never reads file bytes, never calls a provider, never touches storage keys, and
/// never registers itself anywhere. Fail-closed invariants: an identifier is an address, not an
/// access grant — anything this path cannot confirm on server state denies, and a returned record
/// contains only facts the server itself stored about the asset.
/// </summary>
public static class MusicReferenceAssetAuthorizationCodes
{
    /// <summary>Names the service that produced a verified access record, mirroring the
    /// <c>VerifiedByService</c> convention used by the reference eligibility policy.</summary>
    public const string VerifiedByService = "music_reference_asset_authorization";

    // Path-local denial codes. They describe why an asset identifier could not be resolved into a
    // verified record on server state. They are distinct from — and never replace — the
    // MUSIC_REFERENCE_* eligibility codes owned by PR #776's policy or the MUSIC_* request codes
    // owned by the generation validator; a later adapter surfaces all three layers side by side.
    public const string PrincipalInvalid = "MUSIC_REFERENCE_AUTH_PRINCIPAL_INVALID";
    public const string SelectionsMissing = "MUSIC_REFERENCE_AUTH_SELECTIONS_MISSING";
    public const string SelectionLimitExceeded = "MUSIC_REFERENCE_AUTH_SELECTION_LIMIT";
    public const string SelectionInvalid = "MUSIC_REFERENCE_AUTH_SELECTION_INVALID";
    public const string SelectionDuplicated = "MUSIC_REFERENCE_AUTH_SELECTION_DUPLICATED";
    public const string AssetNotAvailable = "MUSIC_REFERENCE_AUTH_ASSET_NOT_AVAILABLE";
    public const string WorkspaceMismatch = "MUSIC_REFERENCE_AUTH_WORKSPACE_MISMATCH";
    public const string AssetArchived = "MUSIC_REFERENCE_AUTH_ASSET_ARCHIVED";
    public const string FileUnavailable = "MUSIC_REFERENCE_AUTH_FILE_UNAVAILABLE";
    public const string FileLineageMismatch = "MUSIC_REFERENCE_AUTH_FILE_LINEAGE_MISMATCH";
    public const string MediaKindMismatch = "MUSIC_REFERENCE_AUTH_MEDIA_KIND_MISMATCH";

    /// <summary>
    /// Structural lookup bound for one authorization request. It mirrors the per-request
    /// reference ceiling of PR #709's validator purely so a hostile list cannot fan out into an
    /// unbounded number of asset lookups; it is not an eligibility rule and must not be treated
    /// as one. The eligibility policy (#776) keeps its own, stricter, configurable ceiling.
    /// </summary>
    public const int MaxLookupsPerRequest = 10;
}

/// <summary>
/// What the caller asked to use as a reference: a bare media kind claim plus an asset identifier.
/// Like the eligibility policy's selector, this is never evidence of anything — it is only the
/// subject the returned records talk about.
/// </summary>
public sealed record MusicReferenceAssetSelection(string? MediaType, Guid AssetId);

/// <summary>
/// A verified reference asset: the requester may read this asset inside the requested workspace,
/// and every media field below was recorded by the server itself (file processing at upload or
/// generation time), never claimed by the client.
/// <list type="bullet">
/// <item><see cref="ContentType"/> and <see cref="SizeBytes"/> always carry server-recorded
/// values.</item>
/// <item><see cref="ContainerFormat"/> and <see cref="DurationSeconds"/> are null when the server
/// never probed them. Null means "not on record", and callers must not substitute a guess: the
/// duration and audio-track facts of a reference video, for instance, are only established by a
/// real inspection step (PR #774's extraction primitive is that step for video).</item>
/// <item><see cref="AudioTrackDetected"/> is true only for assets whose recorded MIME kind is
/// audio. For video it stays null until an actual probe records a track list.</item>
/// </list>
/// Deliberately absent: storage keys, provider details, file names, local paths, URLs, lyrics,
/// and any text derived from the asset content.
/// </summary>
public sealed record MusicReferenceAuthorizedAsset(
    Guid AssetId,
    Guid WorkspaceId,
    string RequestedMediaType,
    string ContentType,
    string? ContainerFormat,
    long SizeBytes,
    double? DurationSeconds,
    bool? AudioTrackDetected,
    string VerifiedByService);

/// <summary>
/// Why one selection (or the whole request) could not be verified. Codes are stable; add, never
/// renumber. Messages are fixed text: they never repeat an asset identifier, a workspace, or any
/// storage detail.
/// </summary>
public sealed record MusicReferenceAuthorizationDenial(string Code, string Message);

/// <summary>
/// The complete outcome of one authorization request. <see cref="AllAuthorized"/> is true only
/// when at least one selection was submitted, every selection resolved, and nothing denied —
/// partial success is reported so a caller sees all outstanding problems in one pass, matching
/// the eligibility policy's aggregation convention.
/// </summary>
public sealed record MusicReferenceAssetAuthorizationOutcome(
    bool AllAuthorized,
    IReadOnlyList<MusicReferenceAuthorizedAsset> Assets,
    IReadOnlyList<MusicReferenceAuthorizationDenial> Denials);

/// <summary>Resolves reference asset identifiers against the shared asset authorization rules.</summary>
public interface IMusicReferenceAssetAuthorizer
{
    Task<MusicReferenceAssetAuthorizationOutcome> AuthorizeAsync(
        Guid userId,
        Guid workspaceId,
        IReadOnlyList<MusicReferenceAssetSelection?> selections,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default authorizer. Ownership goes through <see cref="IAssetService.GetAsync"/> — the same
/// visibility expression and workspace-membership check the asset library and every other studio
/// already use — so this slice cannot invent a weaker rule, and it reads no bytes and calls no
/// provider. Everything beyond the lookup (workspace fence, lifecycle state, storage lineage,
/// recorded media kind) is then verified against the server's own rows.
/// </summary>
public sealed class MusicReferenceAssetAuthorizer(IAssetService assets) : IMusicReferenceAssetAuthorizer
{
    public async Task<MusicReferenceAssetAuthorizationOutcome> AuthorizeAsync(
        Guid userId,
        Guid workspaceId,
        IReadOnlyList<MusicReferenceAssetSelection?> selections,
        CancellationToken cancellationToken = default)
    {
        var authorized = new List<MusicReferenceAuthorizedAsset>();
        var denials = new List<MusicReferenceAuthorizationDenial>();

        if (userId == Guid.Empty || workspaceId == Guid.Empty)
        {
            denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.PrincipalInvalid,
                "The reference request is missing an authenticated principal or workspace."));
            return Finish(authorized, denials);
        }

        if (selections is null || selections.Count == 0)
        {
            denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.SelectionsMissing,
                "At least one reference asset must be selected."));
            return Finish(authorized, denials);
        }

        if (selections.Count > MusicReferenceAssetAuthorizationCodes.MaxLookupsPerRequest)
        {
            denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.SelectionLimitExceeded,
                "Too many reference assets were submitted for one request."));
            return Finish(authorized, denials);
        }

        var seen = new HashSet<Guid>();
        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mediaType = selection?.MediaType?.Trim().ToLowerInvariant();
            var isAudio = string.Equals(mediaType, "audio", StringComparison.Ordinal);
            var isVideo = string.Equals(mediaType, "video", StringComparison.Ordinal);
            if (selection is null || (!isAudio && !isVideo) || selection.AssetId == Guid.Empty)
            {
                denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.SelectionInvalid,
                    "A reference must name audio or video and a valid asset identifier."));
                continue;
            }

            if (!seen.Add(selection.AssetId))
            {
                denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.SelectionDuplicated,
                    "The same reference asset was submitted more than once."));
                continue;
            }

            var asset = await assets.GetAsync(userId, selection.AssetId, cancellationToken);
            if (asset is null)
            {
                // Covers unknown ids, other users' personal assets, and assets the shared
                // visibility rules already refuse. This path reports only "not available".
                denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.AssetNotAvailable,
                    "A reference asset was not found in your accessible library."));
                continue;
            }

            if (asset.WorkspaceId != workspaceId)
            {
                denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.WorkspaceMismatch,
                    "A reference asset belongs to a different workspace than the request."));
                continue;
            }

            if (asset.Status != AssetStatus.Active)
            {
                denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.AssetArchived,
                    "A reference asset is archived and cannot be used."));
                continue;
            }

            var file = asset.StoredFile;
            if (file is null || file.Status != StoredFileStatus.Ready || file.SizeBytes <= 0)
            {
                denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.FileUnavailable,
                    "A reference asset file is not ready for use."));
                continue;
            }

            if (file.WorkspaceId != asset.WorkspaceId)
            {
                denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.FileLineageMismatch,
                    "A reference asset's file does not belong to the asset's workspace."));
                continue;
            }

            var storedKind = MediaKind(file.ContentType);
            var assetKind = string.IsNullOrWhiteSpace(asset.MimeType) ? storedKind : MediaKind(asset.MimeType);
            if (storedKind is null || assetKind is null || storedKind != assetKind || storedKind != mediaType)
            {
                // The requested kind, the asset's recorded MIME type, and the stored file's
                // recorded content type must all agree on server facts alone. Anything that
                // disagrees, or that the server cannot classify, denies.
                denials.Add(Deny(MusicReferenceAssetAuthorizationCodes.MediaKindMismatch,
                    "A reference asset's recorded media kind does not match the requested type."));
                continue;
            }

            authorized.Add(new MusicReferenceAuthorizedAsset(
                asset.Id,
                asset.WorkspaceId,
                mediaType,
                file.ContentType.Trim().ToLowerInvariant(),
                NormalizeContainer(file.ContainerFormat),
                file.SizeBytes,
                file.DurationSeconds,
                isAudio ? true : null,
                MusicReferenceAssetAuthorizationCodes.VerifiedByService));
        }

        return Finish(authorized, denials);
    }

    private static MusicReferenceAssetAuthorizationOutcome Finish(
        List<MusicReferenceAuthorizedAsset> authorized,
        List<MusicReferenceAuthorizationDenial> denials) =>
        new(denials.Count == 0 && authorized.Count > 0, authorized, denials);

    private static MusicReferenceAuthorizationDenial Deny(string code, string message) => new(code, message);

    private static string? MediaKind(string? contentType)
    {
        var value = contentType?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "audio";
        if (value.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "video";
        return null;
    }

    private static string? NormalizeContainer(string? containerFormat)
    {
        var value = containerFormat?.Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
