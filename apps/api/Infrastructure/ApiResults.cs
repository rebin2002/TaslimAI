using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;

namespace Taslim.Api.Infrastructure;

public static class ApiResults
{
    public static IActionResult Error(ControllerBase controller, int statusCode, string code, string message) =>
        controller.StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));

    public static IActionResult Validation(ControllerBase controller, string message = "Please check the highlighted fields.") =>
        Error(controller, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", message);
}
