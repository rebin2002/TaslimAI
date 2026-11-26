using System.Text.Json;
using System.Text.Json.Nodes;
using Taslim.Api.Domain;
using Taslim.Api.Generation;

namespace Taslim.Api.Movies;

/// <summary>
/// Request-identity rules for replaying a Movie scene/shot clip request that carries
/// an <c>Idempotency-Key</c>. The legacy Studio queue persists the <see cref="MovieClip"/>
/// row before it creates the generation job, so a retry can arrive while the key is
/// already claimed by a job whose clip row is either linked, or still unlinked because
/// the process failed between the two commits. These rules decide whether the claimed
/// job is the same generation request, which is the only case in which an existing clip
/// may be reused. Comparison is limited to values this path actually persists in the job
/// row and its <see cref="MovieGenerationInput"/>, so no legitimate replay can be refused
/// over a value the queue never recorded. Provider selection, pricing quotes and
/// continuity projection equality are deliberately out of scope: each belongs to its own
/// lane and none of them is part of duplicate-clip reconciliation.
/// </summary>
internal static class MovieStudioClipIdempotency
{
    /// <summary>Mirrors the shared generation job key limit in GenerationJobService.</summary>
    internal const int MaxIdempotencyKeyLength = 80;

    internal const string ReusedCode = "IDEMPOTENCY_KEY_REUSED";
    internal const string InvalidKeyCode = "IDEMPOTENCY_KEY_INVALID";
    internal const string MissingTargetCode = "IDEMPOTENCY_TARGET_UNAVAILABLE";

    internal const string ReusedMessage =
        "This idempotency key already belongs to a different Movie clip generation request.";

    internal const string InvalidKeyMessage = "The idempotency key is too long.";

    internal const string MissingTargetMessage =
        "The idempotent generation target is no longer available.";

    /// <summary>
    /// Trims an idempotency key and rejects an over-long one before any Movie row is
    /// written. The shared job service applies the same limit, but it only learns the key
    /// after this queue has already inserted a speculative clip, so that rejection used to
    /// strand the clip. Validating here keeps the failure side-effect free.
    /// </summary>
    internal static string? NormalizeKey(string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        var normalized = idempotencyKey.Trim();
        if (normalized.Length > MaxIdempotencyKeyLength)
            throw new GenerationJobValidationException(InvalidKeyCode, InvalidKeyMessage);
        return normalized;
    }

    /// <summary>
    /// Reads the generation input recorded on a claimed job. A job that is not a scene or
    /// shot clip generation, or whose input cannot be parsed, is never a reconciliation
    /// candidate and is therefore treated as a different request.
    /// </summary>
    internal static bool TryReadClipRequest(GenerationJob job, out MovieGenerationInput input)
    {
        input = null!;
        if (string.IsNullOrWhiteSpace(job.InputJson)) return false;
        MovieGenerationInput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null
            || !string.Equals(parsed.Operation, MovieStudioOperations.SceneClip, StringComparison.Ordinal)
            || parsed.MovieClipId == Guid.Empty)
            return false;

        input = parsed;
        return true;
    }

    /// <summary>
    /// A clip row may adopt the canonical job only while it is unlinked (the crash window
    /// this reconciliation repairs) or when it is already linked to that exact job. A clip
    /// linked to a different job is never adopted.
    /// </summary>
    internal static bool IsCompatibleClipLink(Guid? linkedJobId, Guid canonicalJobId) =>
        !linkedJobId.HasValue || linkedJobId.Value == canonicalJobId;

    /// <summary>
    /// True when the claimed job was created for this exact clip row. The shared job
    /// service is idempotent at the job row, so two identical requests can receive the
    /// same job; only the request that owns the clip named in that job may link it.
    /// </summary>
    internal static bool JobTargetsClip(GenerationJob job, Guid clipId) =>
        clipId != Guid.Empty && TryReadClipRequest(job, out var input) && input.MovieClipId == clipId;

    /// <summary>
    /// Confirms a claimed job describes this same target and request: identical job title,
    /// movie, scene, shot, priced duration, description, movie direction, scene snapshot and
    /// shot snapshot. An edited scene, shot, duration or movie direction is a different
    /// generation request and must not adopt the earlier clip.
    /// </summary>
    internal static bool MatchesRequest(
        GenerationJob job,
        MovieProject movie,
        MovieScene scene,
        MovieShot? shot,
        string? requestedTitle,
        string? sceneJson,
        string? shotJson)
    {
        if (!TryReadClipRequest(job, out var input)) return false;
        var expectedTitle = string.IsNullOrWhiteSpace(requestedTitle) ? movie.Title : requestedTitle.Trim();
        if (!string.Equals(job.Title, expectedTitle, StringComparison.Ordinal)) return false;
        if (input.MovieProjectId != movie.Id) return false;
        if (input.MovieSceneId != scene.Id) return false;
        if (input.MovieShotId != shot?.Id) return false;
        if (input.DurationSeconds != ResolveDurationSeconds(movie, scene, shot)) return false;
        if (!string.Equals(input.Description, shot?.Description ?? scene.Summary, StringComparison.Ordinal)) return false;
        if (!string.Equals(input.AspectRatio, movie.AspectRatio, StringComparison.Ordinal)) return false;
        if (!string.Equals(input.Style, movie.Style, StringComparison.Ordinal)) return false;
        if (!string.Equals(input.Language, movie.Language, StringComparison.Ordinal)) return false;
        if (!string.Equals(input.AdditionalInstructions, movie.AdditionalInstructions, StringComparison.Ordinal)) return false;
        if (!string.Equals(input.SceneJson, sceneJson, StringComparison.Ordinal)) return false;
        return string.Equals(
            WithoutWorkflowState(input.ShotJson),
            WithoutWorkflowState(shotJson),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The duration this queue prices and persists for the current target. The identity
    /// check and the clip insert share one derivation so a replay can never compare two
    /// different readings of the same value.
    /// </summary>
    internal static int ResolveDurationSeconds(MovieProject movie, MovieScene scene, MovieShot? shot) =>
        Math.Clamp(shot?.DurationSeconds ?? scene.DurationSeconds ?? Math.Min(movie.DurationSeconds, 60), 1, 3600);

    /// <summary>
    /// Drops the production-contract projection from a serialized shot snapshot. That
    /// projection restates shot content beside approval and stage state, and its stage
    /// state moves as a side effect of queueing, reviewing or rendering other takes of the
    /// same shot. Every generation-intent column (description, purpose, subjects, framing,
    /// camera motion, cinematography, narration, dialogue, continuity notes, quality
    /// requirements and screen direction) is serialized beside it, so removing the
    /// projection keeps a shot content edit a different request without letting unrelated
    /// workflow movement make an identical replay look like one.
    /// </summary>
    internal static string? WithoutWorkflowState(string? shotJson)
    {
        if (string.IsNullOrWhiteSpace(shotJson)) return shotJson;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(shotJson);
        }
        catch (JsonException)
        {
            return shotJson;
        }

        if (root is not JsonObject shot) return shotJson;
        shot.Remove("productionContract");
        return shot.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
