using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Authorization;
using Taslim.Api.Infrastructure;
using Taslim.Api.Music;

namespace Taslim.Api.Controllers;

/// <summary>
/// The user-facing, read-only API boundary for Music Studio reference selection (issue #708,
/// acceptance criteria 3 and 4). One question, answered from server evidence before any paid
/// generation admission is reached: can this already-stored private workspace audio or video asset
/// be used as a song reference, and if not, which fail-closed reason applies?
///
/// Scope limits that the rest of the product must not overread:
/// <list type="bullet">
/// <item>GET only, no state change, no side effects: it creates no asset, no job, no usage row, no
/// billing entry, and no generation admission. It is the pre-flight check the guided Music UI runs
/// against a selection, not the submission itself.</item>
/// <item>It does not upload, accept bytes, extract audio from video, name or call a provider, or
/// enable sung generation. Nothing in this response can make a provider call legal, affordable or
/// possible; an eligible answer means "no server-recorded blocker at selection time", nothing more,
/// and rights attestation plus a verified provider contract still gate the actual request path.</item>
/// <item>Only identifiers the requester can already resolve are ever confirmed. Assets outside the
/// requester's accessible library collapse into the authorizer's uniform not-available refusal, and
/// the body carries only server-recorded facts plus fixed reason text.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize]
[Route("api/music-references")]
public sealed class MusicReferenceEligibilityController(
    IMusicReferenceEligibilityBoundary boundary,
    WorkspaceAccessService workspaceAccess) : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, MusicReferenceIntent> Intents =
        new Dictionary<string, MusicReferenceIntent>(StringComparer.OrdinalIgnoreCase)
        {
            ["style-conditioning"] = MusicReferenceIntent.StyleConditioning,
            ["melody-resemblance"] = MusicReferenceIntent.MelodyResemblance,
            ["authorized-cover"] = MusicReferenceIntent.AuthorizedCoverOrRearrangement,
            ["named-voice-imitation"] = MusicReferenceIntent.NamedVoiceImitation,
        };

    /// <summary>
    /// Evaluates one selection of up to <see cref="MusicReferenceAssetAuthorizationCodes.MaxLookupsPerRequest"/>
    /// references. <c>assetId</c> is required; <c>mediaType</c> is the client's claim about each asset
    /// (audio or video) and is confirmed or refused against server-recorded MIME facts, never trusted.
    /// The answer is always the same envelope shape, so a client can render blockers without
    /// switching on transport status.
    /// </summary>
    [HttpGet("eligibility")]
    public async Task<IActionResult> Evaluate(
        [FromQuery] Guid workspaceId,
        [FromQuery] string? assetId,
        [FromQuery] string? mediaType,
        [FromQuery] string? intent,
        CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Authenticated user identifier is missing."));

        if (!TryParseGuidList(assetId, out var assetIds))
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest,
                MusicReferenceBoundaryCodes.SelectionMalformed,
                "Reference asset identifiers must be valid identifiers.");
        }

        if (assetIds.Count == 0)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest,
                MusicReferenceBoundaryCodes.SelectionMalformed,
                "Each reference must declare audio or video alongside its asset identifier.");
        }

        // Refusing an oversized list at the transport edge means not one identifier is looked up
        // before the request is rejected; the authorizer repeats the same ceiling as defense in depth.
        if (assetIds.Count > MusicReferenceAssetAuthorizationCodes.MaxLookupsPerRequest)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest,
                MusicReferenceBoundaryCodes.SelectionTooLarge,
                "Too many references were declared for one request.");
        }

        var declaredKinds = SplitList(mediaType);
        if (declaredKinds.Count != assetIds.Count)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest,
                MusicReferenceBoundaryCodes.SelectionMalformed,
                "Each reference must declare audio or video alongside its asset identifier.");
        }

        MusicReferenceIntent requestedIntent = MusicReferenceIntent.StyleConditioning;
        if (!string.IsNullOrWhiteSpace(intent) && !Intents.TryGetValue(intent.Trim(), out requestedIntent))
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest,
                MusicReferenceBoundaryCodes.IntentUnsupported,
                "That reference reuse goal is not a supported selection intent.");
        }

        // Membership is fenced before any asset lookup so a non-member learns nothing about whether
        // the referenced identifiers resolve at all. The check is the shared one every studio uses.
        if (!await workspaceAccess.IsMemberAsync(userId, workspaceId, cancellationToken))
        {
            return ApiResults.Error(this, StatusCodes.Status403Forbidden,
                MusicReferenceBoundaryCodes.WorkspaceDenied,
                "You are not a member of the workspace used for this reference request.");
        }

        var selections = assetIds.Zip(declaredKinds, (id, kind) => new MusicReferenceBoundarySelection(kind, id)).ToList();

        var decision = await boundary.EvaluateAsync(
            userId,
            workspaceId,
            selections,
            requestedIntent,
            cancellationToken);

        return Ok(new MusicReferenceEligibilityResponse(
            decision.IsEligible,
            decision.WorkspaceId,
            decision.VerifiedFacts.Select(fact => new MusicReferenceVerifiedFactDto(
                fact.AssetId,
                fact.MediaType,
                fact.ContentType,
                fact.ContainerFormat,
                fact.SizeBytes,
                fact.DurationSeconds,
                fact.AudioTrackDetected)).ToList(),
            decision.Blockers.Select(blocker => new MusicReferenceBlockerDto(
                blocker.Code,
                blocker.Message,
                blocker.AssetId)).ToList()));
    }

    private static IReadOnlyList<string> SplitList(string? raw) => string.IsNullOrWhiteSpace(raw)
        ? Array.Empty<string>()
        : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseGuidList(string? raw, out IReadOnlyList<Guid> values)
    {
        var parsedValues = new List<Guid>();
        values = parsedValues;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        foreach (var part in SplitList(raw))
        {
            if (!Guid.TryParse(part, out var parsed))
            {
                return false;
            }

            parsedValues.Add(parsed);
        }

        return true;
    }
}

/// <summary>
/// Stable transport codes owned by this boundary only. They describe malformed client input and
/// workspace fencing; every substantive refusal uses the authorizer's
/// <c>MUSIC_REFERENCE_AUTH_*</c> codes or the eligibility policy's <c>MUSIC_REFERENCE_*</c> codes,
/// so no reason code is ever duplicated or redefined here.
/// </summary>
public static class MusicReferenceBoundaryCodes
{
    public const string SelectionMalformed = "MUSIC_REFERENCE_BOUNDARY_SELECTION_MALFORMED";
    public const string SelectionTooLarge = "MUSIC_REFERENCE_BOUNDARY_SELECTION_TOO_LARGE";
    public const string IntentUnsupported = "MUSIC_REFERENCE_BOUNDARY_INTENT_UNSUPPORTED";
    public const string WorkspaceDenied = "MUSIC_REFERENCE_BOUNDARY_WORKSPACE_DENIED";
}

/// <summary>
/// Response contract. Declared here rather than in <c>Contracts/MusicGenerationContracts.cs</c>,
/// which belongs to the open song-request contract slice; this boundary must not edit it.
/// </summary>
public sealed record MusicReferenceEligibilityResponse(
    bool IsEligible,
    Guid WorkspaceId,
    IReadOnlyList<MusicReferenceVerifiedFactDto> VerifiedFacts,
    IReadOnlyList<MusicReferenceBlockerDto> Blockers);

public sealed record MusicReferenceVerifiedFactDto(
    Guid AssetId,
    string MediaType,
    string ContentType,
    string? ContainerFormat,
    long SizeBytes,
    double? DurationSeconds,
    bool? AudioTrackDetected);

public sealed record MusicReferenceBlockerDto(string Code, string Message, Guid? AssetId);
