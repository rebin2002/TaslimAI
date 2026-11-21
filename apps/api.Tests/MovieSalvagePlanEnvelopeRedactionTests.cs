using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Synthetic regressions for the POST /api/movie-director/salvage-plan error envelope.
/// The endpoint must answer the repo's stable {error:{code,message}} contract and must never
/// echo .NET framework argument-guard text back to clients: ArgumentNullException/ArgumentException
/// messages embed internal parameter names (for example "Value cannot be null. (Parameter
/// 'request.Context')"), which is exactly the kind of implementation detail the redaction
/// contract keeps server-side. Boot style follows MovieDirectorIntercutControllerTests:
/// direct controller activation against the real deterministic salvage service, no providers,
/// no database, no charges.
/// </summary>
public sealed class MovieSalvagePlanEnvelopeRedactionTests
{
    private const string InvalidCode = "MOVIE_SALVAGE_REQUEST_INVALID";

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

    private static ErrorEnvelope AssertStableRejection(IActionResult result)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);

        // Consistency: the rejection must be the repo ErrorEnvelope, never a framework
        // ValidationProblemDetails/Problem shape leaking through this curated boundary.
        var envelope = Assert.IsType<ErrorEnvelope>(objectResult.Value);
        Assert.Equal(InvalidCode, envelope.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Error.Message));

        // Redaction: framework guard text (parameter names, stack fragments, exception type
        // names) must never appear in the client-visible message.
        var message = envelope.Error.Message;
        Assert.False(message.Contains("Parameter", StringComparison.Ordinal), $"Leaked framework parameter detail: '{message}'");
        Assert.False(message.Contains("Value cannot be null", StringComparison.Ordinal), $"Leaked ArgumentNullException text: '{message}'");
        Assert.False(message.Contains("request.Context", StringComparison.Ordinal), $"Leaked internal member name: '{message}'");
        Assert.False(message.Contains("MovieSalvageDirector", StringComparison.Ordinal), $"Leaked internal type name: '{message}'");
        Assert.False(message.Contains("at Taslim", StringComparison.Ordinal), $"Leaked stack fragment: '{message}'");
        return envelope;
    }

    [Fact]
    public void Missing_edit_context_rejects_with_stable_envelope_and_no_parameter_detail()
    {
        var controller = CreateController();

        // Reproduction: a JSON body that sets "context": null deserializes into a request whose
        // Context is null; the service guard ArgumentNullException.ThrowIfNull(request.Context)
        // throws and the controller previously echoed exception.Message verbatim.
        var result = controller.CreateSalvagePlan(new MovieSalvageDirectorRequest { Context = null! });

        AssertStableRejection(result);
    }

    [Fact]
    public void Missing_request_body_rejects_with_stable_envelope_and_no_parameter_detail()
    {
        var controller = CreateController();

        // Reproduction: the other argument guard (ArgumentNullException.ThrowIfNull(request))
        // carries "(Parameter 'request')" — an internal name with no client value.
        var result = controller.CreateSalvagePlan(null!);

        AssertStableRejection(result);
    }

    [Fact]
    public void Rejection_keeps_the_optional_fields_member_absent()
    {
        var controller = CreateController();

        var result = controller.CreateSalvagePlan(new MovieSalvageDirectorRequest { Context = null! });

        var envelope = AssertStableRejection(result);
        Assert.Null(envelope.Error.Fields);
    }

    [Fact]
    public void Valid_request_still_returns_the_deterministic_salvage_plan()
    {
        var controller = CreateController();

        var result = controller.CreateSalvagePlan(new MovieSalvageDirectorRequest
        {
            IssueType = MovieSalvageIssueTypes.Duration,
            Context = new MovieSalvageEditContext
            {
                HasUsableRange = true,
                HasCleanCutPoint = true,
            },
        });

        // Regression guard: the redaction must not swallow legitimate salvage plans.
        var ok = Assert.IsType<OkObjectResult>(result);
        var plan = Assert.IsType<MovieSalvagePlan>(ok.Value);
        Assert.Equal(MovieSalvageDirectorContract.Version, plan.ContractVersion);
        Assert.Equal(MovieSalvageIssueTypes.Duration, plan.IssueType);
        Assert.Contains(MovieSalvageActionTypes.Trim, plan.Options.Select(option => option.Action));
        Assert.Contains(MovieSalvageActionTypes.Regenerate, plan.Options.Select(option => option.Action));
    }
}
