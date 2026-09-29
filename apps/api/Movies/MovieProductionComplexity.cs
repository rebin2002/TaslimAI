using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieProductionComplexityDimensions
{
    public const string MotionComplexity = "motion_complexity";
    public const string CameraComplexity = "camera_complexity";
    public const string FaceImportance = "face_importance";
    public const string HandBodyInteractionComplexity = "hand_body_interaction_complexity";
    public const string FineDetailImportance = "fine_detail_importance";
    public const string EnvironmentComplexity = "environment_complexity";
    public const string VfxComplexity = "vfx_complexity";
    public const string ContinuitySensitivity = "continuity_sensitivity";
    public const string TextSignageSensitivity = "text_signage_sensitivity";
    public const string DialogueLipSyncDependency = "dialogue_lip_sync_dependency";
    public const string DurationComplexity = "duration_complexity";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MotionComplexity, CameraComplexity, FaceImportance, HandBodyInteractionComplexity,
        FineDetailImportance, EnvironmentComplexity, VfxComplexity, ContinuitySensitivity,
        TextSignageSensitivity, DialogueLipSyncDependency, DurationComplexity,
    };
}

public static class MovieProductionComplexityBands
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Low, Medium, High,
    };

    public static string ForScore(int score) => score switch
    {
        <= 33 => Low,
        <= 66 => Medium,
        _ => High,
    };
}

public static class MovieProductionComplexitySources
{
    public const string Manual = "Manual";
    public const string DirectorProposal = "DirectorProposal";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Manual, DirectorProposal,
    };
}

public sealed class MovieProductionComplexityProfileRequest
{
    public int? MotionComplexity { get; set; }
    public int? CameraComplexity { get; set; }
    public int? FaceImportance { get; set; }
    public int? HandBodyInteractionComplexity { get; set; }
    public int? FineDetailImportance { get; set; }
    public int? EnvironmentComplexity { get; set; }
    public int? VfxComplexity { get; set; }
    public int? ContinuitySensitivity { get; set; }
    public int? TextSignageSensitivity { get; set; }
    public int? DialogueLipSyncDependency { get; set; }
    public int? DurationComplexity { get; set; }
    public string? DeclaredOverallBand { get; set; }
    public IReadOnlyList<MovieProductionComplexityEvidenceRequest>? Evidence { get; set; }
    public string? Source { get; set; }
}

public sealed record MovieProductionComplexityEvidenceRequest(string Dimension, string Reason, string? Evidence = null);
public sealed record MovieProductionComplexityEvidence(string Dimension, string Reason, string? Evidence = null);
public sealed record MovieProductionComplexityReason(string Dimension, int Score, string Band, string Reason, string? Evidence = null);

public sealed record MovieProductionComplexityProfile(
    int MotionComplexity,
    int CameraComplexity,
    int FaceImportance,
    int HandBodyInteractionComplexity,
    int FineDetailImportance,
    int EnvironmentComplexity,
    int VfxComplexity,
    int ContinuitySensitivity,
    int TextSignageSensitivity,
    int DialogueLipSyncDependency,
    int DurationComplexity,
    int OverallScore,
    string OverallBand,
    IReadOnlyList<MovieProductionComplexityReason> Reasons,
    IReadOnlyList<MovieProductionComplexityEvidence> Evidence);

public sealed record MovieProductionComplexityValidationFinding(string Code, string Field, string Message);

public sealed record MovieProductionComplexityValidationResult(
    bool IsValid,
    MovieProductionComplexityProfile? Profile,
    IReadOnlyList<MovieProductionComplexityValidationFinding> Findings)
{
    public IReadOnlyList<string> ReasonCodes => Findings.Select(item => item.Code).Distinct(StringComparer.Ordinal).ToArray();
}

public static class MovieProductionComplexityValidationCodes
{
    public const string MissingField = "COMPLEXITY_FIELD_MISSING";
    public const string OutOfRange = "COMPLEXITY_SCORE_OUT_OF_RANGE";
    public const string UnsupportedCategory = "COMPLEXITY_CATEGORY_UNSUPPORTED";
    public const string ContradictoryProfile = "COMPLEXITY_PROFILE_CONTRADICTORY";
    public const string MalformedEvidence = "COMPLEXITY_EVIDENCE_INVALID";
    public const string MalformedJson = "COMPLEXITY_JSON_INVALID";
    public const string UnsupportedSource = "COMPLEXITY_SOURCE_UNSUPPORTED";
}

/// <summary>
/// Provider-neutral, deterministic validation boundary for per-shot production complexity.
/// Scores are normalized integers in the inclusive range 0..100. The aggregate is an equally
/// weighted mean so a future Adaptive Resolution Director can consume the profile without
/// inheriting a provider or pricing policy.
/// </summary>
public static class MovieProductionComplexityValidator
{
    public const int MinimumScore = 0;
    public const int MaximumScore = 100;
    public const int MaximumEvidenceItems = 24;
    public const int MaximumReasonLength = 500;
    public const int MaximumEvidenceLength = 1_000;

    private static readonly (string Dimension, Func<MovieProductionComplexityProfileRequest, int?> Value)[] Dimensions =
    [
        (MovieProductionComplexityDimensions.MotionComplexity, request => request.MotionComplexity),
        (MovieProductionComplexityDimensions.CameraComplexity, request => request.CameraComplexity),
        (MovieProductionComplexityDimensions.FaceImportance, request => request.FaceImportance),
        (MovieProductionComplexityDimensions.HandBodyInteractionComplexity, request => request.HandBodyInteractionComplexity),
        (MovieProductionComplexityDimensions.FineDetailImportance, request => request.FineDetailImportance),
        (MovieProductionComplexityDimensions.EnvironmentComplexity, request => request.EnvironmentComplexity),
        (MovieProductionComplexityDimensions.VfxComplexity, request => request.VfxComplexity),
        (MovieProductionComplexityDimensions.ContinuitySensitivity, request => request.ContinuitySensitivity),
        (MovieProductionComplexityDimensions.TextSignageSensitivity, request => request.TextSignageSensitivity),
        (MovieProductionComplexityDimensions.DialogueLipSyncDependency, request => request.DialogueLipSyncDependency),
        (MovieProductionComplexityDimensions.DurationComplexity, request => request.DurationComplexity),
    ];

    public static MovieProductionComplexityValidationResult Validate(MovieProductionComplexityProfileRequest? request)
    {
        var findings = new List<MovieProductionComplexityValidationFinding>();
        if (request is null)
            return Invalid(findings, MovieProductionComplexityValidationCodes.MalformedJson, "profile", "A complexity profile is required.");

        foreach (var (dimension, value) in Dimensions)
        {
            var score = value(request);
            if (!score.HasValue)
            {
                findings.Add(new(MovieProductionComplexityValidationCodes.MissingField, dimension, "Every complexity dimension must be provided."));
            }
            else if (score.Value is < MinimumScore or > MaximumScore)
            {
                findings.Add(new(MovieProductionComplexityValidationCodes.OutOfRange, dimension, "Complexity scores must be between 0 and 100 inclusive."));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Source) && !MovieProductionComplexitySources.Supported.Contains(request.Source.Trim()))
            findings.Add(new(MovieProductionComplexityValidationCodes.UnsupportedSource, "source", "The assessment source is not supported."));

        var evidence = request.Evidence ?? [];
        if (evidence.Count > MaximumEvidenceItems)
            findings.Add(new(MovieProductionComplexityValidationCodes.MalformedEvidence, "evidence", $"Evidence cannot contain more than {MaximumEvidenceItems} items."));
        foreach (var item in evidence)
        {
            if (item is null)
            {
                findings.Add(new(MovieProductionComplexityValidationCodes.MalformedEvidence, "evidence", "Evidence items cannot be null."));
                continue;
            }
            var dimension = item.Dimension?.Trim() ?? string.Empty;
            if (!MovieProductionComplexityDimensions.All.Contains(dimension))
                findings.Add(new(MovieProductionComplexityValidationCodes.MalformedEvidence, "evidence.dimension", "Evidence must reference a supported complexity dimension."));
            if (string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Trim().Length > MaximumReasonLength)
                findings.Add(new(MovieProductionComplexityValidationCodes.MalformedEvidence, "evidence.reason", $"Evidence reasons are required and limited to {MaximumReasonLength} characters."));
            if (item.Evidence?.Length > MaximumEvidenceLength)
                findings.Add(new(MovieProductionComplexityValidationCodes.MalformedEvidence, "evidence.evidence", $"Evidence detail is limited to {MaximumEvidenceLength} characters."));
        }

        if (findings.Count > 0) return new(false, null, findings);

        var scores = Dimensions.ToDictionary(item => item.Dimension, item => item.Value(request)!.Value, StringComparer.Ordinal);
        var overallScore = (int)Math.Round(scores.Values.Average(), MidpointRounding.AwayFromZero);
        var overallBand = MovieProductionComplexityBands.ForScore(overallScore);
        if (!string.IsNullOrWhiteSpace(request.DeclaredOverallBand))
        {
            var declared = request.DeclaredOverallBand.Trim();
            if (!MovieProductionComplexityBands.All.Contains(declared))
                findings.Add(new(MovieProductionComplexityValidationCodes.UnsupportedCategory, "declaredOverallBand", "Overall band must be Low, Medium, or High."));
            else if (!string.Equals(declared, overallBand, StringComparison.OrdinalIgnoreCase))
                findings.Add(new(MovieProductionComplexityValidationCodes.ContradictoryProfile, "declaredOverallBand", $"Declared overall band conflicts with the normalized score band {overallBand}."));
        }

        if (findings.Count > 0) return new(false, null, findings);

        var reasons = Dimensions.Select(item =>
        {
            var score = scores[item.Dimension];
            var band = MovieProductionComplexityBands.ForScore(score);
            var evidenceText = evidence.Where(entry => string.Equals(entry.Dimension?.Trim(), item.Dimension, StringComparison.Ordinal)).Select(entry => entry.Evidence ?? entry.Reason).FirstOrDefault();
            return new MovieProductionComplexityReason(item.Dimension, score, band, $"{item.Dimension} is {band.ToLowerInvariant()} at {score}/100.", evidenceText);
        }).ToArray();
        var normalizedEvidence = evidence.Select(item => new MovieProductionComplexityEvidence(item.Dimension.Trim(), item.Reason.Trim(), string.IsNullOrWhiteSpace(item.Evidence) ? null : item.Evidence.Trim())).ToArray();
        var profile = new MovieProductionComplexityProfile(
            scores[MovieProductionComplexityDimensions.MotionComplexity], scores[MovieProductionComplexityDimensions.CameraComplexity], scores[MovieProductionComplexityDimensions.FaceImportance],
            scores[MovieProductionComplexityDimensions.HandBodyInteractionComplexity], scores[MovieProductionComplexityDimensions.FineDetailImportance], scores[MovieProductionComplexityDimensions.EnvironmentComplexity],
            scores[MovieProductionComplexityDimensions.VfxComplexity], scores[MovieProductionComplexityDimensions.ContinuitySensitivity], scores[MovieProductionComplexityDimensions.TextSignageSensitivity],
            scores[MovieProductionComplexityDimensions.DialogueLipSyncDependency], scores[MovieProductionComplexityDimensions.DurationComplexity], overallScore, overallBand, reasons, normalizedEvidence);
        return new(true, profile, []);
    }

    public static MovieProductionComplexityValidationResult ValidateJson(string? serializedProfile)
    {
        if (string.IsNullOrWhiteSpace(serializedProfile))
            return Invalid([], MovieProductionComplexityValidationCodes.MalformedJson, "profile", "A serialized complexity profile is required.");
        try
        {
            var request = JsonSerializer.Deserialize<MovieProductionComplexityProfileRequest>(serializedProfile, JsonOptions);
            return Validate(request);
        }
        catch (JsonException)
        {
            return Invalid([], MovieProductionComplexityValidationCodes.MalformedJson, "profile", "The complexity profile must be valid JSON.");
        }
    }

    public static int AggregateScore(MovieProductionComplexityProfile profile) => (int)Math.Round(new[]
    {
        profile.MotionComplexity, profile.CameraComplexity, profile.FaceImportance, profile.HandBodyInteractionComplexity,
        profile.FineDetailImportance, profile.EnvironmentComplexity, profile.VfxComplexity, profile.ContinuitySensitivity,
        profile.TextSignageSensitivity, profile.DialogueLipSyncDependency, profile.DurationComplexity,
    }.Average(), MidpointRounding.AwayFromZero);

    private static MovieProductionComplexityValidationResult Invalid(IReadOnlyList<MovieProductionComplexityValidationFinding> existing, string code, string field, string message) =>
        new(false, null, existing.Concat([new(code, field, message)]).ToArray());

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

public sealed class MovieProductionComplexityAssessment
{
    public Guid Id { get; set; }
    public Guid MovieShotId { get; set; }
    public int Version { get; set; }
    public string Source { get; set; } = MovieProductionComplexitySources.Manual;
    public string ProfileJson { get; set; } = "{}";
    public int OverallScore { get; set; }
    public string OverallBand { get; set; } = MovieProductionComplexityBands.Low;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieShot MovieShot { get; set; } = null!;
}

public sealed record MovieProductionComplexityAssessmentDto(
    Guid Id,
    Guid MovieShotId,
    int Version,
    string Source,
    MovieProductionComplexityProfile Profile,
    DateTime CreatedAt);

public sealed class MovieProductionComplexityValidationException(MovieProductionComplexityValidationResult result)
    : Exception("The production complexity profile did not pass deterministic validation.")
{
    public MovieProductionComplexityValidationResult Result { get; } = result;
}

public static class MovieProductionComplexityProjection
{
    public static MovieProductionComplexityAssessmentDto ToDto(MovieProductionComplexityAssessment assessment)
    {
        var profile = JsonSerializer.Deserialize<MovieProductionComplexityProfile>(assessment.ProfileJson, MovieProductionComplexityValidator.JsonOptions)
            ?? throw new InvalidOperationException("A validated complexity assessment could not be read.");
        return new(assessment.Id, assessment.MovieShotId, assessment.Version, assessment.Source, profile, assessment.CreatedAt);
    }
}

public interface IMovieProductionComplexityService
{
    Task<MovieProductionComplexityAssessmentDto?> GetAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default);
    Task<MovieProductionComplexityAssessmentDto?> SaveAsync(Guid userId, Guid shotId, MovieProductionComplexityProfileRequest request, CancellationToken cancellationToken = default);
}

public sealed class MovieProductionComplexityService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration) : IMovieProductionComplexityService
{
    public async Task<MovieProductionComplexityAssessmentDto?> GetAsync(Guid userId, Guid shotId, CancellationToken cancellationToken = default)
    {
        var shot = await db.MovieShots.AsNoTracking().Include(item => item.Scene).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var assessment = await db.MovieProductionComplexityAssessments.AsNoTracking().Where(item => item.MovieShotId == shotId).OrderByDescending(item => item.Version).FirstOrDefaultAsync(cancellationToken);
        return assessment is null ? null : MovieProductionComplexityProjection.ToDto(assessment);
    }

    public async Task<MovieProductionComplexityAssessmentDto?> SaveAsync(Guid userId, Guid shotId, MovieProductionComplexityProfileRequest request, CancellationToken cancellationToken = default)
    {
        var shot = await db.MovieShots.Include(item => item.Scene).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var validation = MovieProductionComplexityValidator.Validate(request);
        if (!validation.IsValid || validation.Profile is null) throw new MovieProductionComplexityValidationException(validation);
        var version = (await db.MovieProductionComplexityAssessments.Where(item => item.MovieShotId == shotId).Select(item => (int?)item.Version).MaxAsync(cancellationToken) ?? 0) + 1;
        var now = DateTime.UtcNow;
        var assessment = new MovieProductionComplexityAssessment
        {
            Id = Guid.NewGuid(), MovieShotId = shotId, Version = version,
            Source = string.IsNullOrWhiteSpace(request.Source) ? MovieProductionComplexitySources.Manual : request.Source.Trim(),
            ProfileJson = JsonSerializer.Serialize(validation.Profile, MovieProductionComplexityValidator.JsonOptions),
            OverallScore = validation.Profile.OverallScore, OverallBand = validation.Profile.OverallBand,
            CreatedByUserId = userId, CreatedAt = now,
        };
        db.MovieProductionComplexityAssessments.Add(assessment);
        await db.SaveChangesAsync(cancellationToken);
        return MovieProductionComplexityProjection.ToDto(assessment);
    }
}
