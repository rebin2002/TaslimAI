using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Presentations;

public sealed class PresentationGenerationJobHandler(
    TaslimDbContext db,
    IPresentationPromptBuilder promptBuilder,
    IPresentationGenerationProvider provider,
    IPresentationRenderer renderer,
    IOptions<PresentationGenerationOptions> options) : IGenerationJobHandler
{
    private readonly PresentationGenerationOptions settings = options.Value;

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.PresentationGenerate, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        PresentationGenerationInput input;
        try
        {
            if (!PresentationGenerationContractMapper.TryDeserializeInput(job.InputJson, out var deserialized) || deserialized is null)
                throw new PresentationRequestValidationException(GenerationJobErrorCodes.PresentationRequestInvalid, "The presentation request is invalid.");
            PresentationGenerationRequestValidator.Validate(deserialized, settings);
            input = deserialized;
        }
        catch (PresentationRequestValidationException exception)
        {
            throw new PresentationGenerationStageException(PresentationGenerationStages.Validation, exception.Code, exception.Message, null, exception);
        }
        progress.Report(8);

        var files = await db.StoredFiles.AsNoTracking()
            .Where(file => file.WorkspaceId == job.WorkspaceId && input.AttachmentIds.Contains(file.Id))
            .ToListAsync(cancellationToken);
        if (files.Count != input.AttachmentIds.Count)
            throw new PresentationRequestValidationException(GenerationJobErrorCodes.PresentationAttachmentUnavailable, "One or more source documents are unavailable.");
        if (files.Any(file => !PresentationGenerationDefaults.AttachmentExtensions.Contains(file.Extension)))
            throw new PresentationRequestValidationException(GenerationJobErrorCodes.PresentationAttachmentUnavailable, "Only supported document files can be used as sources.");
        if (files.Any(file => file.Status != StoredFileStatus.Ready))
            throw new PresentationRequestValidationException(GenerationJobErrorCodes.PresentationAttachmentUnavailable, "One or more source documents are not ready yet.");
        if (files.Any(file => file.TextExtractionStatus != FileExtractionStatus.Ready || string.IsNullOrWhiteSpace(file.ExtractedText)))
            throw new PresentationRequestValidationException(GenerationJobErrorCodes.PresentationAttachmentExtractionFailed, "One or more source documents could not be prepared for context.");
        progress.Report(20);

        PresentationProjectContext? project = null;
        if (input.ProjectId.HasValue)
        {
            var projectEntity = await db.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.ProjectId && item.WorkspaceId == job.WorkspaceId, cancellationToken)
                ?? throw new PresentationRequestValidationException("PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");
            project = new PresentationProjectContext(projectEntity.Name, projectEntity.Instructions, projectEntity.ContextNotes);
        }
        var attachmentOrder = input.AttachmentIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        var sources = files.OrderBy(file => attachmentOrder[file.Id]).Select(file => new PresentationSourceContext(file.OriginalFileName, file.Extension, file.ExtractedText!)).ToArray();
        PresentationGenerationPrompt prompt;
        try
        {
            prompt = promptBuilder.Build(input, project, sources, settings);
        }
        catch (PresentationContextLimitException exception)
        {
            throw new PresentationGenerationStageException(PresentationGenerationStages.Context, GenerationJobErrorCodes.PresentationContextTooLarge, "The selected source material is too large.", null, exception);
        }
        progress.Report(30);

        PresentationProviderResult generated;
        try
        {
            generated = await provider.GenerateAsync(prompt, settings, cancellationToken);
        }
        catch (PresentationGenerationStageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception switch
            {
                AiProviderException providerException => PresentationGenerationFailureCodes.ForProvider(providerException),
                AiProviderUnavailableException or AiProviderTimeoutException or AiGenerationException => GenerationJobErrorCodes.PresentationProviderUnavailable,
                _ => GenerationJobErrorCodes.PresentationGenerationFailed,
            };
            throw new PresentationGenerationStageException(PresentationGenerationStages.Provider, code, "The presentation provider could not complete the request.", null, exception);
        }
        try
        {
            PresentationDraftValidator.Validate(generated.Draft, settings);
        }
        catch (PresentationOutputValidationException exception)
        {
            throw new PresentationGenerationStageException(PresentationGenerationStages.DraftValidation, GenerationJobErrorCodes.PresentationOutputInvalid, "The presentation draft did not satisfy the required structure.", generated.Usage, exception);
        }
        progress.Report(65);

        RenderedPresentation rendered;
        try
        {
            rendered = renderer.Render(generated.Draft, input, settings);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PresentationGenerationStageException(PresentationGenerationStages.PptxRender, GenerationJobErrorCodes.PresentationRenderFailed, "The presentation could not be rendered.", generated.Usage, exception);
        }
        if (rendered.Content.Length < 100 || !rendered.RepresentationType.Equals(AssetRepresentationTypes.Pptx, StringComparison.OrdinalIgnoreCase))
            throw new PresentationGenerationStageException(PresentationGenerationStages.PptxRender, GenerationJobErrorCodes.PresentationRenderFailed, "The presentation renderer returned an invalid file.", generated.Usage);

        var language = string.Equals(input.Language, "auto", StringComparison.OrdinalIgnoreCase) ? generated.Draft.Language : input.Language;
        var metadata = JsonSerializer.Serialize(new
        {
            presentationType = input.PresentationType,
            slideCount = generated.Draft.Slides.Count,
            language,
            format = AssetRepresentationTypes.Pptx,
            generatedAt = DateTime.UtcNow,
        });
        var output = new GenerationHandlerOutput(
            GenerationJobOutputTypes.StoredFile,
            null,
            metadata,
            new GeneratedFileArtifact(rendered.FileName, rendered.ContentType, rendered.Content, metadata, rendered.RepresentationType),
            new GeneratedAssetDescriptor(generated.Draft.Title, generated.Draft.Subtitle, AssetTypes.Presentation, metadata));

        var result = JsonSerializer.Serialize(new
        {
            presentationType = input.PresentationType,
            title = generated.Draft.Title,
            subtitle = generated.Draft.Subtitle,
            language,
            slideCount = generated.Draft.Slides.Count,
            previewSlides = generated.Draft.Slides.Take(6).Select(slide => new
            {
                order = slide.Order,
                type = slide.Type,
                title = slide.Title,
                subtitle = slide.Subtitle,
                blocks = slide.Blocks.Take(3),
            }),
        });
        progress.Report(90);
        progress.Report(100);
        return new GenerationHandlerResult(result, [output], generated.Usage);
    }
}
