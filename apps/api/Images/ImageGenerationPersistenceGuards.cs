using Microsoft.EntityFrameworkCore;

namespace Taslim.Api.Images;

public static class ImageGenerationPersistenceGuards
{
    public const string ActiveJobConflictCode = "IMAGE_GENERATION_IN_PROGRESS";
    public const string ActiveJobConflictMessage = "An image generation is already in progress in this workspace. Wait for it to finish before starting another.";
    private const string ImageStudioRequestPrefix = "image-studio:";

    private const string ActiveJobIndexName = "IX_GenerationJobs_ImageActiveByUser";

    public static string BuildImageStudioRequestId(string traceIdentifier) =>
        $"{ImageStudioRequestPrefix}{traceIdentifier}"[..Math.Min(128, ImageStudioRequestPrefix.Length + traceIdentifier.Length)];

    public static bool IsActiveJobConflict(DbUpdateException exception)
    {
        var details = exception.ToString();
        return details.Contains(ActiveJobIndexName, StringComparison.OrdinalIgnoreCase)
            || details.Contains("GenerationJobs", StringComparison.OrdinalIgnoreCase)
                && details.Contains("WorkspaceId", StringComparison.OrdinalIgnoreCase)
                && details.Contains("CreatedByUserId", StringComparison.OrdinalIgnoreCase)
                && details.Contains("JobType", StringComparison.OrdinalIgnoreCase);
    }
}
