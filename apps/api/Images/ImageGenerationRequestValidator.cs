using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Images;

public static class ImageGenerationRequestValidator
{
    public static void Validate(ImageGenerationInput request, ImageGenerationOptions options)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length < 3 || request.Description.Length > options.MaxPromptCharacters)
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, "Describe the image in 3 to 4,000 characters.");
        if (request.ReferenceFileId.HasValue)
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageReferenceNotSupported, "Reference images are not available in this first Image Studio release.");
        if (request.ProjectId.HasValue && request.ProjectId == Guid.Empty)
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, "The project selection is invalid.");
        ValidateChoice(request.Style, ImageGenerationValues.Styles, "style", "IMAGE_STYLE_UNSUPPORTED");
        ValidateChoice(request.AspectRatio, ImageGenerationValues.AspectRatios, "aspect ratio", "IMAGE_ASPECT_RATIO_UNSUPPORTED");
        ValidateChoice(request.Quality, ImageGenerationValues.Qualities, "quality", "IMAGE_QUALITY_UNSUPPORTED");
        if (request.Title?.Length > options.MaxTitleCharacters)
            throw new ImageRequestValidationException(GenerationJobErrorCodes.ImageRequestInvalid, "The image title is too long.");
    }

    private static void ValidateChoice(string? value, IReadOnlySet<string> supported, string field, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || !supported.Contains(value.Trim()))
            throw new ImageRequestValidationException(code, $"The selected {field} is not supported.");
    }
}
