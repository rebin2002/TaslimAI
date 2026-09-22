using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Images;
using Taslim.Api.Infrastructure;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/image-generation")]
public sealed class ImageGenerationController(
    IGenerationJobService jobs,
    IOptions<ImageGenerationOptions> options) : ControllerBase
{
    [HttpPost("jobs")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromBody] ImageGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var input = ImageGenerationContractMapper.ToInput(request);
            ImageGenerationRequestValidator.Validate(input, options.Value);
            var job = await jobs.CreateAsync(GetUserId(), new CreateGenerationJobRequest
            {
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                JobType = GenerationJobTypes.ImageGenerate,
                Title = request.Title,
                InputJson = System.Text.Json.JsonSerializer.Serialize(input),
            }, cancellationToken);
            return Accepted(new CreateImageGenerationResponse(GenerationJobContractMapper.ToDto(job)));
        }
        catch (ImageRequestValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
        catch (GenerationJobValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
        catch (GenerationJobForbiddenException)
        {
            return Forbid();
        }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));

}
