using System.Text.Json;
using Taslim.Api.Contracts;
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
}
