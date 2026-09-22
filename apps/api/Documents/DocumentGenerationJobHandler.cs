using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Documents;

public sealed class DocumentGenerationJobHandler(
    TaslimDbContext db,
    IDocumentPromptBuilder promptBuilder,
    IDocumentGenerationProvider provider,
    IDocumentRenderer renderer,
    IOptions<DocumentGenerationOptions> options) : IGenerationJobHandler
{
    private readonly DocumentGenerationOptions settings = options.Value;

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.DocumentGenerate, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        DocumentGenerationInput input;
        try
        {
            if (!DocumentGenerationContractMapper.TryDeserializeInput(job.InputJson, out var deserialized) || deserialized is null)
                throw new DocumentRequestValidationException(GenerationJobErrorCodes.DocumentRequestInvalid, "The document request is invalid.");
            DocumentGenerationRequestValidator.Validate(deserialized, settings);
            input = deserialized;
        }
        catch (DocumentRequestValidationException exception)
        {
            throw new DocumentGenerationStageException(DocumentGenerationStages.Validation, exception.Code, exception.Message, null, exception);
        }
        progress.Report(5);
        progress.Report(10);

        var files = await db.StoredFiles.AsNoTracking()
            .Where(file => file.WorkspaceId == job.WorkspaceId && input.AttachmentIds.Contains(file.Id))
            .ToListAsync(cancellationToken);
        if (files.Count != input.AttachmentIds.Count)
            throw new DocumentRequestValidationException(GenerationJobErrorCodes.DocumentAttachmentUnavailable, "One or more source documents are unavailable.");
        if (files.Any(file => !DocumentGenerationDefaults.AttachmentExtensions.Contains(file.Extension)))
            throw new DocumentRequestValidationException(GenerationJobErrorCodes.DocumentAttachmentUnavailable, "Only supported document files can be used as sources.");
        if (files.Any(file => file.Status != StoredFileStatus.Ready))
            throw new DocumentRequestValidationException(GenerationJobErrorCodes.DocumentAttachmentUnavailable, "One or more source documents are not ready yet.");
        if (files.Any(file => file.TextExtractionStatus != FileExtractionStatus.Ready || string.IsNullOrWhiteSpace(file.ExtractedText)))
            throw new DocumentRequestValidationException(GenerationJobErrorCodes.DocumentAttachmentExtractionFailed, "One or more source documents could not be prepared for context.");
        progress.Report(20);

        DocumentProjectContext? project = null;
        if (input.ProjectId.HasValue)
        {
            var projectEntity = await db.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.ProjectId && item.WorkspaceId == job.WorkspaceId, cancellationToken)
                ?? throw new DocumentRequestValidationException("PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");
            project = new DocumentProjectContext(projectEntity.Name, projectEntity.Instructions, projectEntity.ContextNotes);
        }
        var attachmentOrder = input.AttachmentIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        var sources = files.OrderBy(file => attachmentOrder[file.Id])
            .Select(file => new DocumentSourceContext(file.OriginalFileName, file.Extension, file.ExtractedText!))
            .ToArray();
        DocumentGenerationPrompt prompt;
        try
        {
            prompt = promptBuilder.Build(input, project, sources, settings);
        }
        catch (DocumentContextLimitException exception)
        {
            throw new DocumentGenerationStageException(DocumentGenerationStages.Context, GenerationJobErrorCodes.DocumentContextTooLarge, "The selected document context is too large.", null, exception);
        }
        progress.Report(30);

        DocumentProviderResult generated;
        try
        {
            generated = await provider.GenerateAsync(prompt, settings, cancellationToken);
        }
        catch (DocumentGenerationStageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception switch
            {
                AiProviderException providerException => DocumentGenerationFailureCodes.ForProvider(providerException),
                AiProviderUnavailableException or AiProviderTimeoutException or AiGenerationException => GenerationJobErrorCodes.DocumentProviderUnavailable,
                _ => GenerationJobErrorCodes.DocumentGenerationFailed,
            };
            throw new DocumentGenerationStageException(DocumentGenerationStages.Provider, code, "The document provider could not complete the request.", null, exception);
        }
        try
        {
            DocumentDraftValidator.Validate(generated.Draft, settings);
        }
        catch (DocumentOutputValidationException exception)
        {
            throw new DocumentGenerationStageException(DocumentGenerationStages.DraftValidation, GenerationJobErrorCodes.DocumentOutputInvalid, "The document draft did not satisfy the required structure.", generated.Usage, exception);
        }
        progress.Report(65);

        var outputs = new List<GenerationHandlerOutput>();
        var rendered = new List<RenderedDocument>();
        if (input.OutputFormat is "docx" or "both")
        {
            try { rendered.Add(renderer.RenderDocx(generated.Draft, input, settings)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new DocumentGenerationStageException(DocumentGenerationStages.DocxRender, GenerationJobErrorCodes.DocumentRenderFailed, "The DOCX document could not be rendered.", generated.Usage, exception);
            }
        }
        if (input.OutputFormat is "pdf" or "both")
        {
            try { rendered.Add(renderer.RenderPdf(generated.Draft, input, settings)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new DocumentGenerationStageException(DocumentGenerationStages.PdfRender, GenerationJobErrorCodes.DocumentRenderFailed, "The PDF document could not be rendered.", generated.Usage, exception);
            }
        }
        if (rendered.Count == 0) throw new DocumentOutputValidationException();

        var metadata = JsonSerializer.Serialize(new
        {
            sourceCount = files.Count,
            language = input.Language,
            outputFormat = input.OutputFormat,
            generatedAt = DateTime.UtcNow,
        });
        for (var index = 0; index < rendered.Count; index++)
        {
            var item = rendered[index];
            outputs.Add(new GenerationHandlerOutput(
                GenerationJobOutputTypes.StoredFile,
                null,
                metadata,
                new GeneratedFileArtifact(item.FileName, item.ContentType, item.Content, metadata, item.RepresentationType),
                index == 0 ? new GeneratedAssetDescriptor(input.Title, generated.Draft.Summary, AssetTypes.Document, metadata) : null));
        }
        progress.Report(90);
        var result = JsonSerializer.Serialize(new
        {
            documentType = AssetTypes.Document,
            title = generated.Draft.Title,
            language = input.Language,
            summary = generated.Draft.Summary,
            sections = generated.Draft.Sections,
        });
        progress.Report(100);
        return new GenerationHandlerResult(result, outputs, generated.Usage);
    }
}
