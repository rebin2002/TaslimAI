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

        if (options.MaxRowsPerBlock is < 1 or > PresentationRendererLimits.MaxRowsPerBlock)
        {
            return ValidateOptionsResult.Fail(
                $"PresentationGeneration:MaxRowsPerBlock must be between 1 and {PresentationRendererLimits.MaxRowsPerBlock} because the PPTX renderer cannot safely lay out more table rows.");
        }

        return ValidateOptionsResult.Success;
    }
}
