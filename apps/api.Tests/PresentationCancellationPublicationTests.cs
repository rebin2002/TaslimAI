using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

// Deterministic coverage for the race between a user cancelling a Presentation
// Studio job and the generated PPTX file being produced for publication. No live
// provider is called and no private data is used.
public sealed class PresentationCancellationPublicationTests
{
    private const string ValidDraftJson = "{\"title\":\"Launch plan\",\"subtitle\":\"Quarterly review\",\"language\":\"en\",\"theme\":\"professional-navy\",\"presentationType\":\"business\",\"slides\":[{\"order\":1,\"type\":\"title\",\"title\":\"Launch plan\",\"subtitle\":\"Quarterly review\",\"blocks\":[],\"notes\":null,\"visualSuggestion\":null,\"sourceRefs\":[]}]}";

    [Fact]
    public async Task Cancellation_during_provider_completion_stops_before_a_generated_file_is_produced()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var renderer = new RecordingPresentationRenderer();
        var handler = CreateHandler(database.Context, renderer, new ScriptedCompletionService(ValidDraftJson, cancellation));
        var job = PresentationJob();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ExecuteAsync(job, new RecordingProgress(), cancellation.Token));

        // Rendering is the last step before a file can be published, so zero renders
        // proves the cancelled attempt never reached generated-file publication.
        Assert.Equal(0, renderer.RenderCount);
    }

    [Fact]
    public async Task Repeated_cancellation_observation_never_produces_a_generated_file()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var renderer = new RecordingPresentationRenderer();
        var handler = CreateHandler(database.Context, renderer, new ScriptedCompletionService(ValidDraftJson));
        var job = PresentationJob();

        for (var attempt = 0; attempt < 2; attempt += 1)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ExecuteAsync(job, new RecordingProgress(), cancellation.Token));
        }

        Assert.Equal(0, renderer.RenderCount);
    }

    [Fact]
    public async Task Uncancelled_attempt_still_produces_the_publishable_file()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var renderer = new RecordingPresentationRenderer();
        var handler = CreateHandler(database.Context, renderer, new ScriptedCompletionService(ValidDraftJson));

        var result = await handler.ExecuteAsync(PresentationJob(), new RecordingProgress(), cancellation.Token);

        Assert.Equal(1, renderer.RenderCount);
        var output = Assert.Single(result.Outputs);
        Assert.NotNull(output.FileArtifact);
        Assert.Equal(AssetRepresentationTypes.Pptx, output.FileArtifact!.RepresentationType);
    }

    private static PresentationGenerationJobHandler CreateHandler(TaslimDbContext db, IPresentationRenderer renderer, IChatCompletionService completion) => new(
        db,
        new PresentationPromptBuilder(),
        new AiPresentationGenerationProvider(completion),
        renderer,
        Options.Create(new PresentationGenerationOptions { Enabled = true, ProviderTimeoutSeconds = 30 }));

    private static GenerationJob PresentationJob()
    {
        var workspaceId = Guid.NewGuid();
        return new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            CreatedByUserId = Guid.NewGuid(),
            JobType = GenerationJobTypes.PresentationGenerate,
            Status = GenerationJobStatus.Running,
            InputJson = PresentationGenerationContractMapper.SerializeInput(new PresentationGenerationInput(
                workspaceId,
                null,
                "Launch plan",
                "Present the launch plan to the operating team.",
                "business",
                "standard",
                "professional",
                "en",
                null,
                null,
                null,
                true,
                true,
                Array.Empty<Guid>())),
        };
    }

    private sealed class TestDatabase(TaslimDbContext context, SqliteConnection connection) : IAsyncDisposable
    {
        public TaslimDbContext Context { get; } = context;

        public static async Task<TestDatabase> OpenAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var context = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(context, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RecordingPresentationRenderer : IPresentationRenderer
    {
        private readonly PresentationRenderer inner = new();

        public int RenderCount { get; private set; }

        public RenderedPresentation Render(PresentationDraft draft, PresentationGenerationInput input, PresentationGenerationOptions options)
        {
            RenderCount += 1;
            return inner.Render(draft, input, options);
        }
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Steps { get; } = [];

        public void Report(int value) => Steps.Add(value);
    }

    private sealed class ScriptedCompletionService(string content, CancellationTokenSource? cancelCallerOnCall = null) : IChatCompletionService
    {
        public int CallCount { get; private set; }

        public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
        {
            CallCount += 1;
            // The provider response is already on its way back when the user's
            // cancellation is recorded: a late completion, not a fresh request.
            cancelCallerOnCall?.Cancel();
            return Task.FromResult(new AiGenerationResult(content, new AiUsageMetadata("presentation-test", "presentation-test", 100, null, 200, 0.002m, 0.002m, 8, "completed", true)));
        }

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }
    }
}
