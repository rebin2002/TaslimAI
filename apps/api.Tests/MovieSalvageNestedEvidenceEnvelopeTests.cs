using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Movies;
using Xunit;
namespace Taslim.Api.Tests;
/// <summary>
/// Synthetic coverage for the *nested* half of the POST /api/movie-director/salvage-plan
/// rejection contract: a request that carries an edit context but whose nested QC-finding
/// evidence is malformed. The plan-level argument guard in <see cref="MovieSalvageDirector"/>
/// only checks that the request and its context object exist, so nested evidence reaches
/// <c>ResolveIssueType</c> unvalidated and is dereferenced there.
/// Client reachability is reproduced through the same System.Text.Json Web defaults the MVC
/// JSON input formatter uses, so the malformed members are exactly what an authenticated
/// caller can send in a JSON body; nothing here touches a provider, the database, billing,
/// or configuration.
/// Two of these tests pin the current, already-correct behaviour so a future nested guard
/// cannot over-reject legitimate requests; two assert the product-safe envelope contract and
/// are RED on purpose until the nested guard exists (test-only lane, no source change here).
/// Boot style matches the other director controller tests: direct controller activation
/// against the real deterministic salvage planner.
/// </summary>
public sealed class MovieSalvageNestedEvidenceEnvelopeTests
{
    private const string InvalidCode = "MOVIE_SALVAGE_REQUEST_INVALID";
    private static readonly string[] ForbiddenPayloadTokens =
    [
        "Parameter", "Value cannot be null", "Object reference", "NullReferenceException",
        "ArgumentException", "System.", "at Taslim", "Taslim.Api", "MovieSalvageDirector",
        "MovieProductionQcFinding", "request.Context", "qcFindings", "apps/api", "/home/",
    ];
    private static MovieDirectorController CreateController()
    {
        var userId = Guid.NewGuid();
        return new MovieDirectorController(null!, new MovieSalvageDirector(), null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                },
            },
        };
    }
    private static MovieSalvageDirectorRequest BindFromJson(string body) =>
        System.Text.Json.JsonSerializer.Deserialize<MovieSalvageDirectorRequest>(
            body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    /// <summary>
    /// Runs the action and fails with the escaping exception type when client-supplied input
    /// leaves the curated boundary as a raw CLR throw instead of a rejection envelope.
    /// </summary>
    private static ErrorEnvelope AssertStableRejection(IActionResult result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var envelope = Assert.IsType<ErrorEnvelope>(objectResult.Value);
        Assert.Equal(InvalidCode, envelope.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Error.Message));
        var payload = System.Text.Json.JsonSerializer.Serialize(envelope);
        Assert.All(ForbiddenPayloadTokens, token =>
            Assert.False(payload.Contains(token, StringComparison.Ordinal), $"Leaked internal detail '{token}' in: {payload}"));
        return envelope;
    }
    private static ErrorEnvelope RejectionFor(MovieSalvageDirectorRequest request)
    {
        Exception? escape = null;
        IActionResult? result = null;
        try
        {
            result = CreateController().CreateSalvagePlan(request);
        }
        catch (Exception exception)
        {
            escape = exception;
        }
        Assert.True(escape is null,
            $"Malformed nested salvage evidence escaped the action as {escape?.GetType().FullName} instead of a "
            + $"{InvalidCode} rejection envelope.");
        return AssertStableRejection(result!);
    }
    [Fact]
    public void Nested_finding_evidence_without_a_category_rejects_with_the_stable_envelope()
    {
        // Reproduction: the default (or omitted) issue type makes ResolveIssueType read the
        // findings, and a finding whose category is JSON null is dereferenced unguarded.
        var request = BindFromJson("""
            {"issueType":"generic","context":{"hasUsableRange":true},
             "qcFindings":[{"reasonCode":null,"category":null,"severity":null}]}
            """);
        Assert.NotNull(request.Context);
        Assert.Null(request.QcFindings[0].Category);
        Assert.Equal(InvalidCode, RejectionFor(request).Error.Code);
    }
    [Fact]
    public void Null_element_inside_nested_finding_evidence_rejects_with_the_stable_envelope()
    {
        var request = BindFromJson("""
            {"context":{"hasUsableRange":true},"qcFindings":[null]}
            """);
        Assert.NotNull(request.Context);
        Assert.Equal(InvalidCode, RejectionFor(request).Error.Code);
    }
    [Fact]
    public void Empty_nested_edit_context_still_returns_a_deterministic_plan()
    {
        // Over-rejection guard: an empty-but-present context object is valid evidence-free
        // input and must keep returning the review-only plan, not a rejection.
        var result = CreateController().CreateSalvagePlan(BindFromJson("""{"context":{}}"""));
        var ok = Assert.IsType<OkObjectResult>(result);
        var plan = Assert.IsType<MovieSalvagePlan>(ok.Value);
        Assert.Equal(MovieSalvageDirectorContract.Version, plan.ContractVersion);
        Assert.Equal(MovieSalvageIssueTypes.Generic, plan.IssueType);
        Assert.Contains(MovieSalvageActionTypes.Regenerate, plan.Options.Select(option => option.Action));
    }
    [Fact]
    public void Out_of_range_nested_numeric_evidence_still_returns_a_deterministic_plan()
    {
        // Over-rejection guard: nested numeric evidence is advisory only, so implausible
        // values keep planning instead of tripping the rejection path.
        var result = CreateController().CreateSalvagePlan(new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Duration,
            Context = new MovieSalvageEditContext
            {
                HasUsableRange = true,
                CurrentDurationSeconds = -1,
                ExpectedDurationSeconds = int.MaxValue,
                CurrentAspectRatio = -0.5m,
                TargetAspectRatio = 0m,
            },
        });
        var ok = Assert.IsType<OkObjectResult>(result);
        var plan = Assert.IsType<MovieSalvagePlan>(ok.Value);
        Assert.Equal(MovieSalvageIssueTypes.Duration, plan.IssueType);
        Assert.NotEmpty(plan.Options);
    }
}
