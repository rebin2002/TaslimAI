using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Social;

public sealed class SocialGenerationJobHandler(
    TaslimDbContext db,
    ISocialPromptBuilder promptBuilder,
    ISocialGenerationProvider provider,
    IOptions<SocialGenerationOptions> socialOptions) : IGenerationJobHandler
{
    private readonly SocialGenerationOptions settings = socialOptions.Value;

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.SocialGenerate, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        SocialGenerationInput input;
        try
        {
            if (!SocialGenerationContractMapper.TryDeserializeInput(job.InputJson, out var deserialized) || deserialized is null)
                throw new SocialRequestValidationException(GenerationJobErrorCodes.SocialRequestInvalid, "The social request is invalid.");
            SocialGenerationRequestValidator.Validate(deserialized, settings);
            if (deserialized.WorkspaceId != job.WorkspaceId) throw new SocialRequestValidationException("WORKSPACE_MISMATCH", "The selected workspace is not available.");
            input = deserialized;
        }
        catch (SocialRequestValidationException exception)
        {
            throw new SocialGenerationStageException(SocialGenerationStages.Validation, exception.Code, exception.Message);
        }
        progress.Report(8);

        var files = await LoadFilesAsync(input, job.WorkspaceId, cancellationToken);
        var assets = await LoadAssetsAsync(input, job.WorkspaceId, cancellationToken);
        progress.Report(18);

        SocialProjectContext? project = null;
        if (input.ProjectId.HasValue)
        {
            var projectEntity = await db.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.ProjectId && item.WorkspaceId == job.WorkspaceId, cancellationToken)
                ?? throw new SocialGenerationStageException(SocialGenerationStages.Validation, "PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");
            project = new SocialProjectContext(projectEntity.Name, projectEntity.Instructions, projectEntity.ContextNotes);
        }

        var sources = files.Select(file => new SocialSourceContext(file.OriginalFileName, "source file", Trim(file.ExtractedText, settings.MaxContextCharacters), null, file.ContentType))
            .Concat(assets.Select(asset => new SocialSourceContext(asset.Name, "asset", Trim(asset.StoredFile?.ExtractedText, settings.MaxContextCharacters), asset.AssetType, asset.MimeType)))
            .ToArray();

        SocialGenerationPrompt prompt;
        try { prompt = promptBuilder.Build(input, project, sources, settings); }
        catch (SocialContextLimitException exception) { throw new SocialGenerationStageException(SocialGenerationStages.Context, GenerationJobErrorCodes.SocialContextTooLarge, "The selected context is too large. Choose fewer or shorter sources.", null, exception); }
        progress.Report(30);

        SocialProviderResult generated;
        try
        {
            generated = await provider.GenerateAsync(prompt, settings, cancellationToken);
        }
        catch (SocialGenerationStageException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception switch
            {
                AiProviderException providerException => SocialGenerationFailureCodes.ForProvider(providerException),
                AiProviderUnavailableException or AiProviderTimeoutException => GenerationJobErrorCodes.SocialProviderUnavailable,
                _ => GenerationJobErrorCodes.SocialGenerationFailed,
            };
            throw new SocialGenerationStageException(SocialGenerationStages.Provider, code, "Social generation could not be completed.", null, exception);
        }
        progress.Report(68);

        try { SocialDraftValidator.Validate(generated.Draft, settings); }
        catch (SocialOutputValidationException exception) { throw new SocialGenerationStageException(SocialGenerationStages.DraftValidation, GenerationJobErrorCodes.SocialOutputInvalid, "The social result did not satisfy the required structure.", generated.Usage, exception); }
        var selectedAssetNames = assets.Select(asset => asset.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (generated.Draft.Posts.SelectMany(post => post.AssetRefs).Any(reference => !selectedAssetNames.Contains(reference)))
            throw new SocialGenerationStageException(SocialGenerationStages.DraftValidation, GenerationJobErrorCodes.SocialOutputInvalid, "The social result referenced an unselected Asset.", generated.Usage);

        var language = NormalizeOutputLanguage(generated.Draft.Language, input.Language);
        generated.Draft.Language = language;
        var metadata = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Social,
            platform = generated.Draft.Platform,
            socialType = generated.Draft.SocialType,
            language,
            postCount = generated.Draft.Posts.Count,
            selectedAssetCount = input.AssetIds.Count,
            selectedSourceCount = input.AttachmentIds.Count,
            generatedAt = DateTime.UtcNow,
        });
        var draftJson = JsonSerializer.Serialize(generated.Draft, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        var safeName = BuildFileName(generated.Draft.Title);
        var artifact = new GeneratedFileArtifact(safeName, "application/json", Encoding.UTF8.GetBytes(draftJson), metadata);
        var asset = new GeneratedAssetDescriptor(generated.Draft.Title, generated.Draft.Posts.FirstOrDefault()?.Hook, AssetTypes.Social, metadata);
        var result = JsonSerializer.Serialize(new
        {
            socialType = generated.Draft.SocialType,
            platform = generated.Draft.Platform,
            title = generated.Draft.Title,
            language,
            postCount = generated.Draft.Posts.Count,
            posts = generated.Draft.Posts,
        });
        progress.Report(92);
        return new GenerationHandlerResult(result, [new GenerationHandlerOutput(GenerationJobOutputTypes.StoredFile, null, metadata, artifact, asset)], generated.Usage);
    }

    private async Task<IReadOnlyList<StoredFile>> LoadFilesAsync(SocialGenerationInput input, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (input.AttachmentIds.Count == 0) return [];
        var files = await db.StoredFiles.AsNoTracking().Where(file => file.WorkspaceId == workspaceId && input.AttachmentIds.Contains(file.Id)).ToListAsync(cancellationToken);
        if (files.Count != input.AttachmentIds.Count || files.Any(file => !SocialGenerationDefaults.AttachmentExtensions.Contains(file.Extension)) || files.Any(file => file.Status != StoredFileStatus.Ready))
            throw new SocialGenerationStageException(SocialGenerationStages.Context, GenerationJobErrorCodes.SocialContextUnavailable, "One or more selected source files are unavailable.");
        if (files.Any(file => file.TextExtractionStatus != FileExtractionStatus.Ready || string.IsNullOrWhiteSpace(file.ExtractedText)))
            throw new SocialGenerationStageException(SocialGenerationStages.Context, GenerationJobErrorCodes.SocialContextUnavailable, "One or more selected source files could not be read.");
        return input.AttachmentIds.Select(id => files.First(file => file.Id == id)).ToArray();
    }

    private async Task<IReadOnlyList<Asset>> LoadAssetsAsync(SocialGenerationInput input, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (input.AssetIds.Count == 0) return [];
        var assets = await db.Assets.AsNoTracking().Include(asset => asset.StoredFile).Where(asset => asset.WorkspaceId == workspaceId && input.AssetIds.Contains(asset.Id)).ToListAsync(cancellationToken);
        if (assets.Count != input.AssetIds.Count || assets.Any(asset => asset.Status != AssetStatus.Active))
            throw new SocialGenerationStageException(SocialGenerationStages.Context, GenerationJobErrorCodes.SocialContextUnavailable, "One or more selected assets are unavailable.");
        return input.AssetIds.Select(id => assets.First(asset => asset.Id == id)).ToArray();
    }

    private static string NormalizeOutputLanguage(string draftLanguage, string requested) => draftLanguage is "en" or "ar" or "ku" ? draftLanguage : requested is "en" or "ar" or "ku" ? requested : "en";
    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..Math.Max(1, max - 3)].TrimEnd() + "...";

    private static string BuildFileName(string title)
    {
        var safe = new string(title.Trim().Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ').ToArray()).Trim().Replace(' ', '-');
        if (string.IsNullOrWhiteSpace(safe)) safe = "social-content";
        return $"{safe[..Math.Min(80, safe.Length)]}.json";
    }
}
