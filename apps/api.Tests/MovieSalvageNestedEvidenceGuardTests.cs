using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Green regression coverage for the fail-closed nested QC-evidence admission added to
/// <see cref="MovieSalvageDirector"/>. The companion test-only lane pins the required public
/// envelope contract from the controller; this file pins the planner-side guarantee itself:
/// malformed nested evidence is rejected as an <see cref="ArgumentException"/> whose message
/// carries no member, parameter, or CLR-type detail, at every caller-facing entry point, and
/// the short-circuited explicit-issue-type path cannot smuggle the same malformed evidence
/// through unexamined. Fully synthetic fixtures; no provider, database, billing, or secrets.
/// </summary>
public sealed class MovieSalvageNestedEvidenceGuardTests
{
    private static readonly string[] ForbiddenMessageTokens =
    [
        "Parameter", "Value cannot be null", "Object reference", "NullReferenceException",
        "ArgumentException", "System.", "at Taslim", "Taslim.Api", "MovieSalvageDirector",
        "MovieProductionQcFinding", "request.Context", "qcFindings", "apps/api", "/home/",
        "ReasonCode", "Category", "Severity",
    ];

    [Theory]
    [MemberData(nameof(MalformedFindings))]
    public void Malformed_nested_finding_is_rejected_fail_closed(MovieProductionQcFinding? finding)
    {
        var director = new MovieSalvageDirector();
        var request = new MovieSalvageDirectorRequest
        {
            Context = new MovieSalvageEditContext { HasUsableRange = true },
            QcFindings = [finding!],
        };

        var rejection = Assert.Throws<ArgumentException>(() => director.Plan(request));

        Assert.False(string.IsNullOrWhiteSpace(rejection.Message));
        Assert.All(ForbiddenMessageTokens, token =>
            Assert.False(rejection.Message.Contains(token, StringComparison.Ordinal),
                $"Rejection message leaked internal detail '{token}': {rejection.Message}"));
    }

    [Theory]
    [InlineData(MovieSalvageIssueTypes.Generic)]
    [InlineData(MovieSalvageIssueTypes.Duration)]
    [InlineData(MovieSalvageIssueTypes.Composition)]
    [InlineData(MovieSalvageIssueTypes.Continuity)]
    [InlineData("not-a-known-issue-type")]
    public void Malformed_nested_finding_is_rejected_even_when_the_issue_type_short_circuits(string issueType)
    {
        // A recognised non-generic issue type returns before the findings scan, and an
        // unrecognised one falls through to generic; every route must still reject the
        // malformed nested evidence instead of silently planning around it.
        var director = new MovieSalvageDirector();
        var request = new MovieSalvageDirectorRequest
        {
            IssueType = issueType,
            Context = new MovieSalvageEditContext { HasUsableRange = true },
            QcFindings = [new MovieProductionQcFinding("MOVIE_QC_HARD_CONTINUITY_CONFLICT", null!, "error")],
        };

        Assert.Throws<ArgumentException>(() => director.Plan(request));
    }

    [Fact]
    public void Decision_entry_point_rejects_malformed_nested_finding()
    {
        // The QC-decision overload reaches the same admission gate as the request overload.
        var director = new MovieSalvageDirector();
        var decision = new MovieProductionQcDecision(
            MovieProductionQualityControlService.ContractVersion,
            MovieProductionQcActions.RequireReview,
            true,
            [null!],
            new MovieResolution(1920, 1080),
            new MovieResolution(1920, 1080),
            null,
            null);

        Assert.Throws<ArgumentException>(() => director.Plan(decision, new MovieSalvageEditContext()));
    }

    [Fact]
    public void Well_formed_finding_evidence_still_resolves_the_inferred_issue_type()
    {
        // Over-rejection guard: the admission gate must keep letting genuine evidence through
        // to the inference it already performs.
        var plan = new MovieSalvageDirector().Plan(new MovieSalvageDirectorRequest
        {
            Context = new MovieSalvageEditContext { HasUsableRange = true },
            QcFindings = [new MovieProductionQcFinding(MovieProductionQcReasonCodes.HardContinuityConflict, "continuity", "error")],
        });

        Assert.Equal(MovieSalvageIssueTypes.Continuity, plan.IssueType);
        Assert.NotEmpty(plan.Options);
    }

    [Fact]
    public void Public_endpoint_answers_malformed_nested_finding_with_the_stable_envelope()
    {
        // The controller half of the guarantee: the ArgumentException becomes the curated
        // product-safe 400 envelope, and the serialized body carries no framework detail.
        var userId = Guid.NewGuid();
        var controller = new MovieDirectorController(null!, new MovieSalvageDirector(), null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                },
            },
        };
        var request = System.Text.Json.JsonSerializer.Deserialize<MovieSalvageDirectorRequest>(
            """
            {"issueType":"generic","context":{"hasUsableRange":true},
             "qcFindings":[{"reasonCode":null,"category":null,"severity":null}]}
            """,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        var result = controller.CreateSalvagePlan(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var envelope = Assert.IsType<ErrorEnvelope>(objectResult.Value);
        Assert.Equal("MOVIE_SALVAGE_REQUEST_INVALID", envelope.Error.Code);
        var payload = System.Text.Json.JsonSerializer.Serialize(envelope);
        Assert.All(ForbiddenMessageTokens, token =>
            Assert.False(payload.Contains(token, StringComparison.Ordinal),
                $"Rejection payload leaked internal detail '{token}': {payload}"));
    }

    public static TheoryData<MovieProductionQcFinding?> MalformedFindings => new()
    {
        null,
        new MovieProductionQcFinding(null!, "continuity", "error"),
        new MovieProductionQcFinding("", "continuity", "error"),
        new MovieProductionQcFinding("MOVIE_QC_ASPECT_RATIO_MISMATCH", null!, "error"),
        new MovieProductionQcFinding("MOVIE_QC_ASPECT_RATIO_MISMATCH", "   ", "error"),
        new MovieProductionQcFinding("MOVIE_QC_ASPECT_RATIO_MISMATCH", "continuity", null!),
    };
}
