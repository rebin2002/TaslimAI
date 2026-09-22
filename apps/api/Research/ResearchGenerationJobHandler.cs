using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Research;

public sealed class ResearchGenerationJobHandler(
    TaslimDbContext db,
    IResearchPlanner planner,
    IResearchSearchProvider searchProvider,
    IResearchContentFetcher contentFetcher,
    IResearchEvidenceProcessor evidenceProcessor,
    IResearchReportProvider reportProvider,
    IDocumentRenderer documentRenderer,
    IOptions<ResearchGenerationOptions> researchOptions,
    IOptions<DocumentGenerationOptions> documentOptions) : IGenerationJobHandler
{
    private readonly ResearchGenerationOptions settings = researchOptions.Value;
    private readonly DocumentGenerationOptions documentSettings = documentOptions.Value;

    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.ResearchGenerate, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        ResearchGenerationInput input;
        try
        {
            if (!ResearchGenerationContractMapper.TryDeserializeInput(job.InputJson, out var deserialized) || deserialized is null)
                throw new ResearchRequestValidationException(GenerationJobErrorCodes.ResearchRequestInvalid, "The research request is invalid.");
            ResearchGenerationRequestValidator.Validate(deserialized, settings);
            input = deserialized;
        }
        catch (ResearchRequestValidationException exception)
        {
            throw new ResearchGenerationStageException(ResearchGenerationStages.Validation, exception.Code, exception.Message);
        }
        progress.Report(5);

        var files = await LoadFilesAsync(input, cancellationToken);
        progress.Report(12);
        ResearchProjectContext? project = null;
        if (input.ProjectId.HasValue)
        {
            var projectEntity = await db.Projects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.ProjectId && item.WorkspaceId == job.WorkspaceId, cancellationToken)
                ?? throw new ResearchRequestValidationException("PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");
            project = new ResearchProjectContext(projectEntity.Name, projectEntity.Instructions, projectEntity.ContextNotes);
        }
        var plan = await planner.PlanAsync(input, project, cancellationToken);
        progress.Report(20);

        var sourceCandidates = new List<ResearchSourceCandidate>();
        var evidenceCandidates = new List<ResearchEvidenceCandidate>();
        var usage = new AiUsageMetadata("system", "research", null, null, null, 0m, 0m, 0, "completed", true, SafeMetadataJson: JsonSerializer.Serialize(new { researchStage = "local_sources" }));
        if (input.UseWebSources)
        {
            try
            {
                var searched = await searchProvider.SearchAsync(new ResearchSearchRequest(input, plan, new ResearchSourcePreference(input.UseWebSources, input.PreferredDomains, input.ExcludedDomains)), settings, cancellationToken);
                sourceCandidates.AddRange(searched.Sources);
                evidenceCandidates.AddRange(searched.Evidence);
                usage = searched.Usage;
            }
            catch (ResearchSearchUnavailableException exception)
            {
                throw new ResearchGenerationStageException(ResearchGenerationStages.Search, GenerationJobErrorCodes.ResearchSearchUnavailable, "Web research is temporarily unavailable.", usage, exception);
            }
            catch (ResearchSearchTimeoutException exception)
            {
                throw new ResearchGenerationStageException(ResearchGenerationStages.Search, GenerationJobErrorCodes.ResearchSearchFailed, "Web research took too long to complete.", usage, exception);
            }
            catch (ResearchSearchFailedException exception)
            {
                throw new ResearchGenerationStageException(ResearchGenerationStages.Search, GenerationJobErrorCodes.ResearchSearchFailed, "Web research could not be completed.", usage, exception);
            }
        }
        progress.Report(42);

        var nextCitation = sourceCandidates.Count + 1;
        foreach (var file in files)
        {
            var citationId = $"S{nextCitation++}";
            var candidate = new ResearchSourceCandidate(citationId, null, null, file.OriginalFileName, "uploaded file", null, null, DateTime.UtcNow, "uploaded", Trim(file.ExtractedText!, settings.MaxSourceSnippetCharacters), Trim(file.ExtractedText!, settings.MaxSourceTextCharacters), null, nextCitation - 1, true, JsonSerializer.Serialize(new { storedFileId = file.Id, fileExtension = file.Extension }));
            sourceCandidates.Add(candidate);
            evidenceCandidates.Add(new ResearchEvidenceCandidate(citationId, "uploaded source", Trim(file.ExtractedText!, settings.MaxEvidenceCharacters), null, null));
        }
        sourceCandidates = sourceCandidates.Take(Math.Min(settings.MaxSourceCount, ResearchGenerationDefaults.MaxSources(input.Depth))).ToList();
        if (sourceCandidates.Count == 0)
            throw new ResearchGenerationStageException(ResearchGenerationStages.Search, GenerationJobErrorCodes.ResearchSourceUnavailable, "No usable research sources were found.", usage);
        evidenceCandidates = evidenceProcessor.Normalize(sourceCandidates, evidenceCandidates, settings).ToList();
        sourceCandidates = (await contentFetcher.FetchAsync(sourceCandidates, settings, cancellationToken)).ToList();
        progress.Report(50);

        await PersistSourcesAsync(job, sourceCandidates, files, evidenceCandidates, cancellationToken);
        progress.Report(58);

        ResearchReportProviderResult report;
        try
        {
            report = await reportProvider.GenerateAsync(new ResearchReportPrompt(input, plan, sourceCandidates, evidenceCandidates, project), settings, cancellationToken);
        }
        catch (ResearchContextLimitException exception)
        {
            throw new ResearchGenerationStageException(ResearchGenerationStages.Context, GenerationJobErrorCodes.ResearchContextTooLarge, "The selected research context is too large.", usage, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception switch
            {
                AiProviderUnavailableException or AiProviderTimeoutException or ResearchSearchUnavailableException => GenerationJobErrorCodes.ResearchProviderUnavailable,
                AiProviderException => GenerationJobErrorCodes.ResearchProviderUnavailable,
                ResearchOutputInvalidException => GenerationJobErrorCodes.ResearchOutputInvalid,
                _ => GenerationJobErrorCodes.ResearchGenerationFailed,
            };
            throw new ResearchGenerationStageException(ResearchGenerationStages.Report, code, "The research report could not be generated.", usage, exception);
        }
        try
        {
            var citationIds = sourceCandidates.Select(source => source.CitationId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ResearchDraftValidator.Validate(report.Draft, citationIds, settings);
        }
        catch (ResearchCitationValidationException exception)
        {
            throw new ResearchGenerationStageException(ResearchGenerationStages.DraftValidation, GenerationJobErrorCodes.ResearchCitationValidationFailed, "The research report citations were invalid.", report.Usage, exception);
        }
        catch (ResearchOutputValidationException exception)
        {
            throw new ResearchGenerationStageException(ResearchGenerationStages.DraftValidation, GenerationJobErrorCodes.ResearchOutputInvalid, "The research report structure was invalid.", report.Usage, exception);
        }
        progress.Report(70);

        var documentDraft = ResearchDocumentMapper.ToDocumentDraft(report.Draft, sourceCandidates);
        var documentInput = new DocumentGenerationInput(job.WorkspaceId, input.ProjectId, report.Draft.Title, input.Question, "report", input.Depth, input.Audience, input.AdditionalInstructions, input.AttachmentIds, NormalizeOutputLanguage(report.Draft.Language, input.Language), "both", "professional", true);
        var rendered = new List<RenderedDocument>();
        try { rendered.Add(documentRenderer.RenderDocx(documentDraft, documentInput, documentSettings)); }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new ResearchGenerationStageException(ResearchGenerationStages.RenderDocx, GenerationJobErrorCodes.ResearchRenderFailed, "The research DOCX report could not be rendered.", report.Usage, exception); }
        try { rendered.Add(documentRenderer.RenderPdf(documentDraft, documentInput, documentSettings)); }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new ResearchGenerationStageException(ResearchGenerationStages.RenderPdf, GenerationJobErrorCodes.ResearchRenderFailed, "The research PDF report could not be rendered.", report.Usage, exception); }

        var metadata = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Research,
            sourceCount = sourceCandidates.Count,
            webSourceCount = sourceCandidates.Count(source => source.SourceType == "web"),
            uploadedSourceCount = sourceCandidates.Count(source => source.SourceType == "uploaded"),
            language = NormalizeOutputLanguage(report.Draft.Language, input.Language),
            reportType = input.ReportType,
            generatedAt = DateTime.UtcNow,
        });
        var outputs = rendered.Select((item, index) => new GenerationHandlerOutput(
            GenerationJobOutputTypes.StoredFile,
            null,
            metadata,
            new GeneratedFileArtifact(item.FileName, item.ContentType, item.Content, metadata, item.RepresentationType),
            index == 0 ? new GeneratedAssetDescriptor(report.Draft.Title, report.Draft.ExecutiveSummary, AssetTypes.Research, metadata) : null)).ToArray();
        progress.Report(90);

        var safeSources = sourceCandidates.Select(source => new ResearchSourceDto(source.CitationId, source.Url, source.Title, source.Domain, source.Publisher, source.PublishedAt, source.RetrievedAt, source.SourceType, source.Snippet, source.SearchQuery, source.Rank, source.IsSelected)).ToArray();
        var result = JsonSerializer.Serialize(new
        {
            researchType = AssetTypes.Research,
            title = report.Draft.Title,
            subtitle = report.Draft.Subtitle,
            language = NormalizeOutputLanguage(report.Draft.Language, input.Language),
            executiveSummary = report.Draft.ExecutiveSummary,
            keyFindings = report.Draft.KeyFindings,
            sections = report.Draft.Sections,
            conclusion = report.Draft.Conclusion,
            sourceCount = safeSources.Length,
            sources = safeSources,
        });
        progress.Report(100);
        return new GenerationHandlerResult(result, outputs, MergeUsage(usage, report.Usage, sourceCandidates.Count));
    }

    private async Task<List<StoredFile>> LoadFilesAsync(ResearchGenerationInput input, CancellationToken cancellationToken)
    {
        if (input.AttachmentIds.Count == 0) return [];
        var files = await db.StoredFiles.AsNoTracking().Where(file => file.WorkspaceId == input.WorkspaceId && input.AttachmentIds.Contains(file.Id)).ToListAsync(cancellationToken);
        if (files.Count != input.AttachmentIds.Count || files.Any(file => !ResearchGenerationDefaults.AttachmentExtensions.Contains(file.Extension)) || files.Any(file => file.Status != StoredFileStatus.Ready))
            throw new ResearchRequestValidationException(GenerationJobErrorCodes.ResearchSourceUnavailable, "One or more selected source files are unavailable.");
        if (files.Any(file => file.TextExtractionStatus != FileExtractionStatus.Ready || string.IsNullOrWhiteSpace(file.ExtractedText)))
            throw new ResearchRequestValidationException(GenerationJobErrorCodes.ResearchSourceExtractionFailed, "One or more selected source files could not be read.");
        var order = input.AttachmentIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        return files.OrderBy(file => order[file.Id]).ToList();
    }

    private async Task PersistSourcesAsync(GenerationJob job, IReadOnlyList<ResearchSourceCandidate> candidates, IReadOnlyList<StoredFile> files, IReadOnlyList<ResearchEvidenceCandidate> evidence, CancellationToken cancellationToken)
    {
        var fileByName = files.GroupBy(file => file.OriginalFileName, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var entities = candidates.Select(candidate => new ResearchSource
        {
            Id = Guid.NewGuid(),
            WorkspaceId = job.WorkspaceId,
            GenerationJobId = job.Id,
            StoredFileId = candidate.SourceType == "uploaded" && fileByName.TryGetValue(candidate.Title, out var file) ? file.Id : null,
            CitationId = candidate.CitationId,
            Url = candidate.Url,
            CanonicalUrl = candidate.CanonicalUrl,
            Title = candidate.Title,
            Domain = candidate.Domain,
            Publisher = candidate.Publisher,
            PublishedAt = candidate.PublishedAt,
            RetrievedAt = candidate.RetrievedAt,
            SourceType = candidate.SourceType,
            Snippet = candidate.Snippet,
            ExtractedText = candidate.ExtractedText,
            SearchQuery = candidate.SearchQuery,
            Rank = candidate.Rank,
            IsSelected = candidate.IsSelected,
            MetadataJson = candidate.MetadataJson,
        }).ToArray();
        db.ResearchSources.AddRange(entities);
        foreach (var item in evidence)
        {
            var source = entities.FirstOrDefault(entity => entity.CitationId.Equals(item.CitationId, StringComparison.OrdinalIgnoreCase));
            if (source is null) continue;
            db.ResearchEvidence.Add(new ResearchEvidence
            {
                Id = Guid.NewGuid(),
                WorkspaceId = job.WorkspaceId,
                GenerationJobId = job.Id,
                ResearchSourceId = source.Id,
                Topic = item.Topic,
                Excerpt = item.Excerpt,
                Context = item.Context,
                PublishedAt = item.PublishedAt,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeOutputLanguage(string draftLanguage, string requested) => draftLanguage is "en" or "ar" or "ku" ? draftLanguage : requested is "en" or "ar" or "ku" ? requested : "en";

    private static AiUsageMetadata MergeUsage(AiUsageMetadata search, AiUsageMetadata report, int sourceCount)
    {
        var inputTokens = AddNullable(search.InputTokens, report.InputTokens);
        var outputTokens = AddNullable(search.OutputTokens, report.OutputTokens);
        var metadata = JsonSerializer.Serialize(new
        {
            researchStages = new[] { "search", "report" },
            searchProvider = search.ProviderKey,
            searchModel = search.ModelKey,
            reportProvider = report.ProviderKey,
            reportModel = report.ModelKey,
            sourceCount,
            searchCostReturned = search.ActualCost.HasValue || search.EstimatedCost.HasValue,
            reportCostReturned = report.ActualCost.HasValue || report.EstimatedCost.HasValue,
        });
        return report with
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            LatencyMs = Math.Min(int.MaxValue, search.LatencyMs + report.LatencyMs),
            SafeMetadataJson = metadata,
        };
    }

    private static int? AddNullable(int? left, int? right) => left.HasValue || right.HasValue ? (left ?? 0) + (right ?? 0) : null;
    private static string Trim(string value, int max) => value.Length <= max ? value : value[..Math.Max(1, max - 3)].TrimEnd() + "...";
}

internal static class ResearchDocumentMapper
{
    public static DocumentDraft ToDocumentDraft(ResearchDraft draft, IReadOnlyList<ResearchSourceCandidate> sources)
    {
        var sections = new List<DocumentSection>
        {
            new() { Heading = "Key findings", Blocks = draft.KeyFindings.Select(ToDocumentBlock).ToList() },
        };
        sections.AddRange(draft.Sections.Select(section => new DocumentSection { Heading = section.Heading, Blocks = section.Blocks.Select(ToDocumentBlock).ToList() }));
        sections.Add(new DocumentSection { Heading = "Sources", Blocks = sources.Select(source => new DocumentBlock { Type = DocumentBlockTypes.Paragraph, Text = $"[{source.CitationId}] {source.Title} — {source.Url ?? "Uploaded source"}" }).ToList() });
        return new DocumentDraft { Title = draft.Title, Summary = AppendCitations(draft.ExecutiveSummary, []) ?? string.Empty, Sections = sections };
    }

    private static DocumentBlock ToDocumentBlock(ResearchReportBlock block) => new()
    {
        Type = block.Type switch
        {
            ResearchBlockTypes.Bullets => DocumentBlockTypes.BulletList,
            ResearchBlockTypes.NumberedList => DocumentBlockTypes.NumberedList,
            ResearchBlockTypes.Table => DocumentBlockTypes.Table,
            _ => DocumentBlockTypes.Paragraph,
        },
        Text = AppendCitations(block.Text, block.CitationIds),
        Items = block.Items?.Select(item => AppendCitations(item, block.CitationIds) ?? string.Empty).ToList(),
        Rows = block.Rows?.Select(row => new DocumentTableRow { Cells = row.Cells.Select(cell => AppendCitations(cell, block.CitationIds) ?? string.Empty).ToList() }).ToList(),
    };

    private static string? AppendCitations(string? text, IReadOnlyList<string> citations) => string.IsNullOrWhiteSpace(text) ? text : citations.Count == 0 ? text : $"{text.Trim()} {string.Join(' ', citations.Select(citation => $"[{citation}]"))}";
}
