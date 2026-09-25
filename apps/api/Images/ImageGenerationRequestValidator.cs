using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Images;

public static class ImageGenerationRequestValidator
{
    public static void Validate(
        ImageGenerationInput request,
        ImageGenerationOptions options,
        ImageGenerationCapabilities? capabilities = null)
    {
        var maxPromptCharacters = Math.Clamp(options.MaxPromptCharacters, 3, 100_000);
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length < 3 || request.Description.Length > maxPromptCharacters)
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, $"Describe the image in 3 to {maxPromptCharacters:N0} characters.");
        if (request.ReferenceFileId.HasValue && (capabilities is null || !capabilities.SupportsReferenceImages))
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageReferenceNotSupported, "Reference images are not available for this image request.");
        if (request.ProjectId.HasValue && request.ProjectId == Guid.Empty)
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, "The project selection is invalid.");
        ValidateChoice(request.Style, ImageGenerationValues.Styles, "style", "IMAGE_STYLE_UNSUPPORTED");
        ValidateChoice(request.AspectRatio, capabilities?.AspectRatios ?? ImageGenerationValues.AspectRatios, "aspect ratio", "IMAGE_ASPECT_RATIO_UNSUPPORTED");
        ValidateChoice(request.Quality, capabilities?.Qualities ?? ImageGenerationValues.Qualities, "quality", "IMAGE_QUALITY_UNSUPPORTED");
        if (capabilities is not null && capabilities.MaxImagesPerRequest < 1)
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, "The selected image configuration is unavailable.");
        if (request.Title?.Length > Math.Max(1, options.MaxTitleCharacters))
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, "The image title is too long.");
    }

    private static void ValidateChoice(string? value, IReadOnlySet<string> supported, string field, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || !supported.Contains(value.Trim()))
            throw new ImageRequestValidationException(code, $"The selected {field} is not supported.");
    }
}
