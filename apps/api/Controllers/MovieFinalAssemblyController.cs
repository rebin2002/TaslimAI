using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Assets;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;
using Taslim.Api.Generation;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-studio")]
public sealed class MovieFinalAssemblyController(
    IMovieFinalAssemblyService assemblies,
    IAssetService assets,
    IFileStorageService storage,
    ILogger<MovieFinalAssemblyController> logger) : ControllerBase
{
    [HttpPost("projects/{id:guid}/final-assembly")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Queue(Guid id, MovieFinalAssemblyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await assemblies.RequestAsync(UserId(), id, request, HttpContext.Request.Headers["Idempotency-Key"].FirstOrDefault(), cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Accepted(result);
        }
        catch (MovieFinalAssemblyValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieCollaborationForbiddenException exception) { return ApiResults.Error(this, 403, "MOVIE_PERMISSION_DENIED", exception.Message); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
    }

    [HttpGet("final-assemblies/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await assemblies.GetAsync(UserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_FINAL_ASSEMBLY_NOT_FOUND", "Final assembly not found.") : Ok(result);
    }

    /// <summary>
    /// Streams the durable final master for a completed assembly. Access is
    /// re-authorized through the assembly read model and the private Asset
    /// download path, so no provider URL or storage key is ever exposed.
    /// </summary>
    [HttpGet("final-assemblies/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var assembly = await assemblies.GetAsync(UserId(), id, cancellationToken);
        if (assembly is null) return ApiResults.Error(this, 404, "MOVIE_FINAL_ASSEMBLY_NOT_FOUND", "Final assembly not found.");
        if (assembly.OutputAssetId is not Guid assetId)
            return ApiResults.Error(this, 409, "MOVIE_FINAL_ASSEMBLY_NOT_READY", "This final assembly does not have a published output yet.");
        var download = await assets.GetDownloadAsync(UserId(), assetId, null, cancellationToken);
        if (download is null) return ApiResults.Error(this, 404, "MOVIE_FINAL_ASSEMBLY_OUTPUT_NOT_FOUND", "The final assembly output is not available.");
        try
        {
            var stream = await storage.OpenReadAsync(download.StoredFile.StorageKey, cancellationToken);
            if (stream is null) return ApiResults.Error(this, 404, "MOVIE_FINAL_ASSEMBLY_OUTPUT_NOT_FOUND", "The final assembly output file could not be found.");
            return File(stream, download.ContentType, download.FileName, enableRangeProcessing: true);
        }
        catch (FileStorageUnavailableException)
        {
            logger.LogWarning("Final assembly download rejected because private storage is unavailable. AssemblyId={AssemblyId}", id);
            return ApiResults.Error(this, 503, "FILE_STORAGE_UNAVAILABLE", "Asset storage is not available right now.");
        }
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}
