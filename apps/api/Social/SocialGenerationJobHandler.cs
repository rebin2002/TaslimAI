using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
namespace Taslim.Api.Social;
public sealed class SocialGenerationJobHandler(
    TaslimDbContext db,
    ISocialPromptBuilder promptBuilder,
    ISocialGenerationProvider provider,
    IOptions<SocialGenerationOptions> socialOptions,
    ILogger<SocialGenerationJobHandler> logger) : IGenerationJobHandler
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

        var files = await LoadFilesAsync(input, job.WorkspaceId, job.CreatedByUserId, cancellationToken);
        var assets = await LoadAssetsAsync(input, job.WorkspaceId, job.CreatedByUserId, cancellationToken);
        progress.Report(18);

        SocialProjectContext? project = null;
        if (input.ProjectId.HasValue)
        {
            var projectEntity = await db.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.ProjectId && item.WorkspaceId == job.WorkspaceId, cancellationToken)
                ?? throw new SocialGenerationStageException(SocialGenerationStages.Validation, "PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");
            project = new SocialProjectContext(projectEntity.Name, projectEntity.Instructions, projectEntity.ContextNotes);
        }

        var sources = files.Select(file => new SocialSourceContext(file.OriginalFileName, "source file", Trim(file.ExtractedText, settings.MaxContextCharacters), null, file.ContentType))
            // An asset contributes its identity only, never its backing file's extracted
            // text. SocialPromptBuilder already refuses to render content for kind "asset";
            // not handing it the text closes the same disclosure path at both ends, which is
            // what lets admission follow the platform's real file-visibility decision instead
            // of an asset/file project-scope equality that legal write flows desynchronise.
            .Concat(assets.Select(asset => new SocialSourceContext(asset.Name, "asset", null, asset.AssetType, asset.MimeType)))
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

    private async Task<IReadOnlyList<StoredFile>> LoadFilesAsync(SocialGenerationInput input, Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        if (input.AttachmentIds.Count == 0) return [];
        var files = await db.StoredFiles.AsNoTracking()
            .Where(file => file.WorkspaceId == workspaceId && input.AttachmentIds.Contains(file.Id))
            .Where(StoredFileVisibility.ForWorkspaceMember(userId))
            .ToListAsync(cancellationToken);
        if (files.Count != input.AttachmentIds.Count || files.Any(file => !SocialGenerationDefaults.AttachmentExtensions.Contains(file.Extension)) || files.Any(file => file.Status != StoredFileStatus.Ready))
            throw new SocialGenerationStageException(SocialGenerationStages.Context, GenerationJobErrorCodes.SocialContextUnavailable, "One or more selected source files are unavailable.");
        if (files.Any(file => file.TextExtractionStatus != FileExtractionStatus.Ready || string.IsNullOrWhiteSpace(file.ExtractedText)))
            throw new SocialGenerationStageException(SocialGenerationStages.Context, GenerationJobErrorCodes.SocialContextUnavailable, "One or more selected source files could not be read.");
        return input.AttachmentIds.Select(id => files.First(file => file.Id == id)).ToArray();
    }

    private async Task<IReadOnlyList<Asset>> LoadAssetsAsync(SocialGenerationInput input, Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        if (input.AssetIds.Count == 0) return [];
        // Selection is decided by the platform's own asset policy rather than by a parallel
        // re-implementation of it. AssetVisibility already answers "may this workspace member
        // use this asset and its backing file at all", including the cross-workspace integrity
        // conjuncts (an asset whose project leaves the workspace, or a non-creator asset whose
        // file is personal or conversation-backed, is not visible). The whole selection then
        // has to survive, so a single inadmissible asset fails the job closed.
        var assets = await db.Assets.AsNoTracking()
            .Include(asset => asset.StoredFile)
            .ThenInclude(storedFile => storedFile!.Project)
            .Where(asset => asset.WorkspaceId == workspaceId && input.AssetIds.Contains(asset.Id))
            .Where(AssetVisibility.ForWorkspaceMember(userId))
            .ToListAsync(cancellationToken);
        if (assets.Count != input.AssetIds.Count)
            throw UnavailableAssets(workspaceId, userId, input.AssetIds.Except(assets.Select(asset => asset.Id)), "not present in this workspace or not visible to this member under the asset policy");
        foreach (var asset in assets)
        {
            if (DescribeAssetRejection(asset, workspaceId, userId) is { } reason)
                throw UnavailableAssets(workspaceId, userId, [asset.Id], reason);
        }
        return input.AssetIds.Select(id => assets.First(asset => asset.Id == id)).ToArray();
    }

    /// <summary>
    /// Why this asset cannot be used in a Social generation, or null when it can.
    /// </summary>
    /// <remarks>
    /// Row #1128 originally added `asset.ProjectId == asset.StoredFile.ProjectId` here,
    /// justified by the backing file's extracted text reaching the prompt. Neither half
    /// holds, and the clause broke legal data:
    /// - The text does not reach the prompt. <see cref="SocialPromptBuilder"/> renders only
    ///   name, type and content type for kind "asset" (pinned by
    ///   SocialGenerationUnitTests.Prompt_builder_keeps_asset_metadata_but_excludes_private_extracted_text),
    ///   and this handler no longer even passes the text to the builder.
    /// - Two supported write flows desynchronise the two columns, and neither can be repaired
    ///   by a write: <c>AssetService.UpdateAsync</c> moves <c>Asset.ProjectId</c> alone, and
    ///   <c>FileProcessingService.FinalizeGeneratedFileAsync</c> adopts the pre-existing
    ///   same-hash row when it dedups generated output, so the asset legitimately points at a
    ///   file whose scope belongs to whoever stored those bytes first. The unique index
    ///   IX_StoredFiles_WorkspaceId_ContentHashSha256 (TaslimDbContext) forbids a per-project
    ///   copy of the same content, which is exactly why the equality is unsatisfiable rather
    ///   than merely inconvenient: every such asset stayed permanently unusable in Social
    ///   Studio behind a generic "unavailable" error while the same member could still
    ///   download that very file through the asset.
    /// The guard that does carry the disclosure risk is the file-visibility decision the
    /// download route and the read model's canDownload flag already share, so this now defers
    /// to <see cref="AssetService.CanReadStoredFile"/>. That is no weaker: it still refuses a
    /// cross-workspace file, and it now also refuses a personal or conversation-backed file the
    /// caller does not own even when the asset is unscoped - the case the old equality let
    /// through (both columns null matched), so this closes a hole rather than opening one.
    /// </remarks>
    private static string? DescribeAssetRejection(Asset asset, Guid workspaceId, Guid userId)
    {
        if (asset.Status != AssetStatus.Active) return "the asset is not active";
        if (!SocialGenerationDefaults.SelectableAssetTypes.Contains(asset.AssetType)) return "the asset type is not selectable in Social Studio";
        if (asset.StoredFileId is null) return "the asset has no backing file";
        if (asset.StoredFile is not { Status: StoredFileStatus.Ready }) return "the backing file is not ready";
        if (!AssetService.CanReadStoredFile(asset.StoredFile, userId, workspaceId)) return "the backing file is outside this member's file visibility";
        return null;
    }

    /// <summary>
    /// Fails the whole selection closed. Clients keep receiving the same static,
    /// provider-neutral message so that asset existence cannot be probed through the error
    /// text; the specific reason is recorded server-side only, and never carries content.
    /// </summary>
    private SocialGenerationStageException UnavailableAssets(Guid workspaceId, Guid userId, IEnumerable<Guid> assetIds, string reason)
    {
        logger.LogWarning("Social asset selection refused. WorkspaceId={WorkspaceId}; UserId={UserId}; AssetIds={AssetIds}; Reason={Reason}", workspaceId, userId, string.Join(',', assetIds), reason);
        return new SocialGenerationStageException(SocialGenerationStages.Context, GenerationJobErrorCodes.SocialContextUnavailable, "One or more selected assets are unavailable.");
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
