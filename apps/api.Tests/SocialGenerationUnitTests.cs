using Taslim.Api.Contracts;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SocialGenerationUnitTests
{
    [Fact]
    public void Prompt_builder_uses_only_selected_context_and_never_personal_memory()
    {
        var input = new SocialGenerationInput(Guid.NewGuid(), null, "Announce the launch", "announcement", "linkedin", "professional", "en", "operators", null, null, true, false, false, [], []);
        var prompt = new SocialPromptBuilder().Build(input, new SocialProjectContext("Launch", "Use approved facts", "Audience: operators"), [new SocialSourceContext("brief.txt", "source file", "Only selected source")], new SocialGenerationOptions());
        Assert.Contains("Only selected source", prompt.UserInstruction, StringComparison.Ordinal);
        Assert.Contains("Audience: operators", prompt.UserInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("personal memory", prompt.UserInstruction, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<SocialContextLimitException>(() => new SocialPromptBuilder().Build(input, null, [new SocialSourceContext("large.txt", "source file", new string('x', 2_001))], new SocialGenerationOptions { MaxContextCharacters = 2_000 }));
    }

    [Fact]
    public void Structured_social_schema_is_strict_and_requires_canonical_post_fields()
    {
        var schema = SocialDraftStructuredOutput.Spec.Schema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "title", "platform", "socialType", "language", "posts" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        var post = schema.GetProperty("properties").GetProperty("posts").GetProperty("items");
        Assert.False(post.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("visualDirection", post.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Draft_validator_rejects_html_and_wrong_order()
    {
        var html = new SocialDraft { Title = "Launch", Platform = "linkedin", SocialType = "announcement", Language = "en", Posts = [new SocialPost { Order = 1, Hook = "<script>", Body = "Body" }] };
        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(html, new SocialGenerationOptions()));
        var wrongOrder = new SocialDraft { Title = "Launch", Platform = "linkedin", SocialType = "announcement", Language = "en", Posts = [new SocialPost { Order = 2, Hook = "Hook", Body = "Body" }] };
        Assert.Throws<SocialOutputValidationException>(() => SocialDraftValidator.Validate(wrongOrder, new SocialGenerationOptions()));
    }
}
