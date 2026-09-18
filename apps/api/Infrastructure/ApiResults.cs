using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;

namespace Taslim.Api.Infrastructure;

public static class ApiResults
{
    public static IActionResult Error(ControllerBase controller, int statusCode, string code, string message, IReadOnlyDictionary<string, string[]>? fields = null) =>
        controller.StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message, fields)));

    public static IActionResult Validation(ControllerBase controller, string message = "Please check the highlighted fields.", IReadOnlyDictionary<string, string[]>? fields = null) =>
        Error(controller, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", message, fields);
}
