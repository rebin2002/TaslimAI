using Microsoft.Extensions.Options;

namespace Taslim.Api.Presentations;

public sealed class PresentationGenerationOptionsValidator : IValidateOptions<PresentationGenerationOptions>
{
    public ValidateOptionsResult Validate(string? name, PresentationGenerationOptions options)
    {
        if (options.MaxBlocksPerSlide is < 1 or > PresentationRendererLimits.MaxBlocksPerSlide)
        {
            return ValidateOptionsResult.Fail(
                $"PresentationGeneration:MaxBlocksPerSlide must be between 1 and {PresentationRendererLimits.MaxBlocksPerSlide} because the PPTX renderer cannot safely lay out more slide-level blocks.");
        }

        return ValidateOptionsResult.Success;
    }
}
