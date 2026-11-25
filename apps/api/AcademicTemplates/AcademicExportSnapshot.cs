using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taslim.Api.AcademicTemplates;

/// <summary>
/// The export-settings state frozen onto a completed research job at export time (the job lane stores it
/// inside its existing ResultJson/settings column — no schema change lives in this PR, see integration
/// notes). "Re-render without re-running the AI" is defined mechanically here: the snapshot pins the
/// versioned template id, the canonical design hash and the source-content hash, and
/// <see cref="AcademicReExportPolicy"/> only authorises the free path when the research payload itself
/// is unchanged.
/// </summary>
public sealed record AcademicExportSnapshot
{
    public const int SchemaVersion = 1;

    public int SnapshotSchemaVersion { get; init; } = SchemaVersion;
    /// <summary>Versioned template identity, e.g. taslim.academic.classic-thesis.v1.</summary>
    public required string TemplateId { get; init; }
    public required string ConfigurationId { get; init; }
    public required string DesignHash { get; init; }
    /// <summary>SHA-256 over the canonical design string, so a tampered snapshot spec is detectable.</summary>
    public required string SpecFingerprint { get; init; }
    public required AcademicDesignSpec Spec { get; init; }
    /// <summary>Student overrides applied on top of the configuration (may be empty, never null).</summary>
    public IReadOnlyDictionary<string, string> StudentOverrides { get; init; } = new Dictionary<string, string>();
    public string? UniversityPresetId { get; init; }
    /// <summary>Identity of the saved research artifact this snapshot re-renders. Not a prompt or model
    /// reference — a workspace file/job handle owned by the research lanes.</summary>
    public required string ResearchArtifactId { get; init; }
    /// <summary>SHA-256 of the saved research content at snapshot time.</summary>
    public required string SourceContentHash { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static AcademicExportSnapshot From(
        AcademicTemplateConfiguration configuration,
        string researchArtifactId,
        string sourceContentHash,
        DateTimeOffset capturedAtUtc,
        IReadOnlyDictionary<string, string>? studentOverrides = null,
        string? universityPresetId = null)
    {
        if (string.IsNullOrWhiteSpace(researchArtifactId)) throw new ArgumentException("A saved research artifact identity is required.", nameof(researchArtifactId));
        if (string.IsNullOrWhiteSpace(sourceContentHash)) throw new ArgumentException("The source content hash is required.", nameof(sourceContentHash));
        var overrides = studentOverrides ?? new Dictionary<string, string>();
        return new AcademicExportSnapshot
        {
            TemplateId = configuration.Spec.TemplateId,
            ConfigurationId = configuration.ConfigurationId,
            DesignHash = configuration.DesignHash,
            SpecFingerprint = configuration.Spec.CanonicalDesignHash(),
            Spec = configuration.Spec,
            StudentOverrides = overrides,
            UniversityPresetId = universityPresetId,
            ResearchArtifactId = researchArtifactId,
            SourceContentHash = sourceContentHash,
            CapturedAtUtc = capturedAtUtc.ToUniversalTime(),
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static AcademicExportSnapshot? FromJson(string json)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize<AcademicExportSnapshot>(json, JsonOptions);
            return snapshot is not null && snapshot.SnapshotSchemaVersion == SchemaVersion ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Canonical hash used to compare "same research, same settings" across re-export attempts.</summary>
    public string SnapshotHash() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f',
            TemplateId, ConfigurationId, DesignHash, ResearchArtifactId, SourceContentHash,
            string.Join(',', StudentOverrides.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))))))
        .ToLowerInvariant();
}

/// <summary>Decision returned to the job lane for one re-export request.</summary>
public sealed record AcademicReExportDecision(
    bool AllowedWithoutGenerationCharge,
    bool TriggersProviderCall,
    string Mode,
    string ReasonCode,
    string? Detail = null);

/// <summary>
/// The no-extra-generation-charge rule, expressed as pure policy so it is testable before the billing
/// lane consumes it: reformatting or re-exporting a saved research artifact with the same source content
/// (different template, different overrides, same document) never queues provider work and never
/// charges a generation credit; a request that would change the research payload is explicitly a new
/// generation and this policy refuses to authorise it as free. The decision struct also carries the
/// invariant that no provider/model/routing detail may accompany it into a customer surface.
/// </summary>
public static class AcademicReExportPolicy
{
    public const string ModeTemplateReformat = "template-reformat";
    public const string ModeReExportSameSettings = "re-export-same-settings";
    public const string ModeNewGenerationRequired = "new-generation-required";
    public const string ModeSnapshotInvalid = "snapshot-invalid";

    public static AcademicReExportDecision Evaluate(AcademicExportSnapshot? snapshot, AcademicTemplateConfiguration? targetConfiguration, string requestSourceContentHash)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.ResearchArtifactId))
            return new AcademicReExportDecision(false, TriggersProviderCall: true, ModeSnapshotInvalid, "SNAPSHOT_MISSING", "The saved job has no academic export snapshot.");
        if (string.IsNullOrWhiteSpace(requestSourceContentHash) || !string.Equals(requestSourceContentHash, snapshot.SourceContentHash, StringComparison.Ordinal))
            return new AcademicReExportDecision(false, TriggersProviderCall: true, ModeNewGenerationRequired, "SOURCE_CHANGED", "The requested source content differs from the saved research artifact; this is a new generation, not a re-export.");
        if (targetConfiguration is null)
            return new AcademicReExportDecision(false, TriggersProviderCall: true, ModeSnapshotInvalid, "TARGET_UNKNOWN", "The requested template configuration is not in the versioned registry.");

        var sameSettings = string.Equals(targetConfiguration.ConfigurationId, snapshot.ConfigurationId, StringComparison.Ordinal);
        return new AcademicReExportDecision(
            AllowedWithoutGenerationCharge: true,
            TriggersProviderCall: false,
            sameSettings ? ModeReExportSameSettings : ModeTemplateReformat,
            sameSettings ? "REEXPORT_IDENTICAL" : "REFORMAT_ONLY",
            "Rendering uses the saved research payload and the snapshot's frozen design spec; no AI stage runs.");
    }
}
