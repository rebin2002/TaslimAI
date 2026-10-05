using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchGenerationUnitTests
{
    [Fact]
    public void Structured_research_schema_is_strict_and_requires_canonical_fields()
    {
        var schema = ResearchStructuredOutput.Spec.Schema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("executiveSummary", schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.False(schema.GetProperty("$defs").GetProperty("block").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void Prompt_builder_uses_explicit_sources_and_bounded_project_context_only()
    {
        var input = new ResearchGenerationInput(Guid.NewGuid(), null, "Compare current solar opportunities", "Solar report", "standard", "market_research", "en", "operators", "Iraq", "2024-2026", null, [], [], true, []);
        var plan = new ResearchPlan(input.Question, "Cited analysis", ["solar Iraq"], ["market"], input.GeographicFocus, input.TimePeriod, ["official"]);
        var sources = new[] { new ResearchSourceCandidate("S1", "https://example.gov/source", "https://example.gov/source", "Official source", "example.gov", null, null, DateTime.UtcNow, "web", "Snippet", "Extracted evidence", "solar Iraq", 1, true, null) };
        var evidence = new[] { new ResearchEvidenceCandidate("S1", "market", "Evidence excerpt", null, null) };
        var prompt = new ResearchPromptBuilder().Build(new ResearchReportPrompt(input, plan, sources, evidence, new ResearchProjectContext("Solar", "Use approved context", "For operators")), new ResearchGenerationOptions());
        Assert.Contains("Evidence excerpt", prompt, StringComparison.Ordinal);
        Assert.Contains("For operators", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("personal memory", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("S1", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Draft_validator_rejects_unknown_citations_and_markup()
    {
        var draft = new ResearchDraft
        {
            Title = "Report",
            Language = "en",
            ExecutiveSummary = "Summary",
            KeyFindings = [new ResearchReportBlock { Type = ResearchBlockTypes.KeyFinding, Text = "Finding", CitationIds = ["S9"] }],
            Sections = [new ResearchSection { Heading = "Section", Blocks = [new ResearchReportBlock { Type = ResearchBlockTypes.Paragraph, Text = "Text", CitationIds = ["S1"] }] }],
            Conclusion = "Conclusion",
            Sources = ["S1"],
        };
        Assert.Throws<ResearchCitationValidationException>(() => ResearchDraftValidator.Validate(draft, new HashSet<string> { "S1" }, new ResearchGenerationOptions()));
        draft.KeyFindings[0].CitationIds = ["S1"];
        draft.KeyFindings[0].Text = "<script>not allowed</script>";
        Assert.Throws<ResearchOutputValidationException>(() => ResearchDraftValidator.Validate(draft, new HashSet<string> { "S1" }, new ResearchGenerationOptions()));
    }

    [Fact]
    public void Evidence_processor_bounds_and_deduplicates_source_context()
    {
        var source = new ResearchSourceCandidate("S1", "https://example.gov", "https://example.gov", "Source", "example.gov", null, null, DateTime.UtcNow, "web", "Snippet", "Extracted", null, 1, true, null);
        var processor = new DeterministicResearchEvidenceProcessor();
        var result = processor.Normalize([source], [new ResearchEvidenceCandidate("S1", "topic", new string('x', 3_000), null, null), new ResearchEvidenceCandidate("S9", "bad", "ignored", null, null)], new ResearchGenerationOptions { MaxEvidenceCharacters = 100 });
        Assert.Single(result);
        Assert.True(result[0].Excerpt.Length <= 100);
        Assert.Equal("S1", result[0].CitationId);
    }

    [Fact]
    public void Evidence_processor_honors_per_source_bound_and_deduplicates_provider_items()
    {
        var sources = new[]
        {
            new ResearchSourceCandidate("S1", "https://example.gov/one", "https://example.gov/one", "One", "example.gov", null, null, DateTime.UtcNow, "web", "Snippet one", "Extracted one", null, 1, true, null),
            new ResearchSourceCandidate("S2", "https://example.gov/two", "https://example.gov/two", "Two", "example.gov", null, null, DateTime.UtcNow, "web", "Snippet two", "Extracted two", null, 2, true, null),
        };
        var providerEvidence = new[]
        {
            new ResearchEvidenceCandidate("S1", "topic", "First claim", null, null),
            new ResearchEvidenceCandidate("S1", "topic", "First claim", null, null),
            new ResearchEvidenceCandidate("S1", "other topic", "Second claim", null, null),
            new ResearchEvidenceCandidate("S2", "topic", "Third claim", null, null),
        };

        var result = new DeterministicResearchEvidenceProcessor().Normalize(sources, providerEvidence, new ResearchGenerationOptions
        {
            MaxEvidencePerSource = 1,
            MaxEvidenceCharacters = 100,
            MaxTotalEvidenceCharacters = 1_000,
        });

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "S1", "S2" }, result.Select(item => item.CitationId));
    }

    [Fact]
    public void Capability_flags_reject_disabled_web_and_uploaded_sources()
    {
        var webInput = new ResearchGenerationInput(Guid.NewGuid(), null, "Valid question", "Report", "standard", "research_report", "en", null, null, null, null, [], [], true, []);
        var webException = Assert.Throws<ResearchRequestValidationException>(() => ResearchGenerationRequestValidator.Validate(webInput, new ResearchGenerationOptions { WebSourcesEnabled = false }));
        Assert.Equal(GenerationJobErrorCodes.ResearchWebSourcesUnavailable, webException.Code);

        var fileInput = webInput with { UseWebSources = false, AttachmentIds = [Guid.NewGuid()] };
        var fileException = Assert.Throws<ResearchRequestValidationException>(() => ResearchGenerationRequestValidator.Validate(fileInput, new ResearchGenerationOptions { UserProvidedSourcesEnabled = false }));
        Assert.Equal(GenerationJobErrorCodes.ResearchUserSourcesUnavailable, fileException.Code);
    }

}
