using System.Runtime.CompilerServices;
using System.Text.Json;
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

/// <summary>
/// Deterministic coverage for the remaining cancellation-versus-late-completion
/// window in Presentation Studio: the instant at which a user cancellation becomes
/// visible is not fixed to the moment the provider response arrives. A cancellation
/// recorded while the late response is still being parsed, validated, rendered or
/// integrity-checked must still settle as a cancellation.
///
/// This matters because the worker reads the provider spend off exactly these
/// exceptions (GenerationJobExecution: "failureUsage ??= (exception as
/// PresentationGenerationStageException)?.Usage") and then finalizes the attempt as
/// Failed with that cost instead of Cancelled with a zeroed usage transaction.
///
/// Everything here is synthetic: the "provider" and the renderer are scripted
/// doubles. No live provider is contacted, no credentials are read, and no customer
/// is charged. No Task.Delay, Thread.Sleep, randomness or wall-clock budget is used;
/// the interleaving is imposed by cancelling the caller's CancellationTokenSource
/// from inside the scripted step that represents "during this phase".
/// </summary>
public sealed class PresentationCancellationFinalizationTests
{
    private const string ValidDraftJson = "{\"title\":\"Launch plan\",\"subtitle\":\"Quarterly review\",\"language\":\"en\",\"theme\":\"professional-navy\",\"presentationType\":\"business\",\"slides\":[{\"order\":1,\"type\":\"title\",\"title\":\"Launch plan\",\"subtitle\":\"Quarterly review\",\"blocks\":[],\"notes\":null,\"visualSuggestion\":null,\"sourceRefs\":[]}]}";

    // Parses and deserializes cleanly but fails the draft validator, so the failure
    // surfaces after the provider boundary rather than inside it.
    private const string DraftInvalidAfterProviderJson = "{\"title\":\"Launch plan\",\"subtitle\":\"Quarterly review\",\"language\":\"not-a-supported-language\",\"theme\":\"professional-navy\",\"presentationType\":\"business\",\"slides\":[{\"order\":1,\"type\":\"title\",\"title\":\"Launch plan\",\"subtitle\":\"Quarterly review\",\"blocks\":[],\"notes\":null,\"visualSuggestion\":null,\"sourceRefs\":[]}]}";

    private static readonly AiUsageMetadata LateCompletionUsage = new("presentation-test", "presentation-test-model", 100, null, 200, 0.002m, 0.002m, 8, "completed", true);

    // ---- race directions: a recorded cancellation must win ----

    [Fact]
    public async Task Cancellation_recorded_while_the_late_response_is_processed_is_not_a_usage_bearing_failure()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(database.Context, new StageThrowingProvider(cancellation), new PassThroughRenderer());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => handler.ExecuteAsync(PresentationJob(), new NullProgress(), cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        AssertCancellationWins(exception);
    }

    [Fact]
    public async Task Cancellation_recorded_during_draft_validation_is_not_a_usage_bearing_failure()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(database.Context, new ScriptedProvider(DraftInvalidAfterProviderJson, cancelCallerBeforeReturn: cancellation), new PassThroughRenderer());

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => handler.ExecuteAsync(PresentationJob(), new NullProgress(), cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        AssertCancellationWins(exception);
    }

    [Fact]
    public async Task Cancellation_recorded_during_rendering_is_not_a_usage_bearing_failure()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(database.Context, new ScriptedProvider(ValidDraftJson), new CancellingThrowingRenderer(cancellation));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => handler.ExecuteAsync(PresentationJob(), new NullProgress(), cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        AssertCancellationWins(exception);
    }

    [Fact]
    public async Task Cancellation_recorded_during_output_integrity_checking_is_not_a_usage_bearing_failure()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(database.Context, new ScriptedProvider(ValidDraftJson), new CancellingTruncatedRenderer(cancellation));

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => handler.ExecuteAsync(PresentationJob(), new NullProgress(), cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        AssertCancellationWins(exception);
    }

    // ---- controls: uncancellation failures keep their existing classification ----

    [Fact]
    public async Task Without_cancellation_a_provider_stage_failure_still_carries_its_usage()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(database.Context, new StageThrowingProvider(cancellationCallerOnCall: null), new PassThroughRenderer());

        var exception = await Assert.ThrowsAsync<PresentationGenerationStageException>(() => handler.ExecuteAsync(PresentationJob(), new NullProgress(), cancellation.Token));

        Assert.Equal(GenerationJobErrorCodes.PresentationOutputInvalid, exception.Code);
        Assert.NotNull(exception.Usage);
    }

    [Fact]
    public async Task Without_cancellation_an_invalid_draft_still_reports_the_output_invalid_stage()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(database.Context, new ScriptedProvider(DraftInvalidAfterProviderJson), new PassThroughRenderer());

        var exception = await Assert.ThrowsAsync<PresentationGenerationStageException>(() => handler.ExecuteAsync(PresentationJob(), new NullProgress(), cancellation.Token));

        Assert.Equal(PresentationGenerationStages.DraftValidation, exception.Stage);
        Assert.Equal(GenerationJobErrorCodes.PresentationOutputInvalid, exception.Code);
        Assert.NotNull(exception.Usage);
    }

    [Fact]
    public async Task Without_cancellation_a_renderer_failure_still_reports_the_render_stage()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(database.Context, new ScriptedProvider(ValidDraftJson), new ThrowingRenderer());

        var exception = await Assert.ThrowsAsync<PresentationGenerationStageException>(() => handler.ExecuteAsync(PresentationJob(), new NullProgress(), cancellation.Token));

        Assert.Equal(GenerationJobErrorCodes.PresentationRenderFailed, exception.Code);
        Assert.NotNull(exception.Usage);
    }

    [Fact]
    public async Task Uncancelled_run_still_returns_the_publishable_artefact()
    {
        await using var database = await TestDatabase.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var handler = CreateHandler(database.Context, new ScriptedProvider(ValidDraftJson), new PassThroughRenderer());

        var result = await handler.ExecuteAsync(PresentationJob(), new NullProgress(), cancellation.Token);

        var output = Assert.Single(result.Outputs);
        Assert.NotNull(output.FileArtifact);
        Assert.Equal(AssetRepresentationTypes.Pptx, output.FileArtifact!.RepresentationType);
    }

    // A cancellation must not be reported as a usage-bearing stage failure, because
    // that is the shape the worker harvests provider spend from and uses to finalize
    // the attempt as Failed.
    private static void AssertCancellationWins(Exception exception)
    {
        Assert.False(exception is PresentationGenerationStageException);
        Assert.Null((exception as PresentationGenerationStageException)?.Usage);
    }

    private static PresentationGenerationJobHandler CreateHandler(TaslimDbContext db, IPresentationGenerationProvider provider, IPresentationRenderer renderer) => new(
        db,
        new PresentationPromptBuilder(),
        provider,
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

    private static PresentationDraft Deserialize(string json) =>
        JsonSerializer.Deserialize<PresentationDraft>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

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

    /// <summary>
    /// Stands in for a provider whose caller cancellation is recorded after its own
    /// cancellation guard but before its parse/validate throw site - the window the
    /// provider-side guard cannot cover.
    /// </summary>
    private sealed class StageThrowingProvider(CancellationTokenSource? cancellationCallerOnCall) : IPresentationGenerationProvider
    {
        public Task<PresentationProviderResult> GenerateAsync(PresentationGenerationPrompt prompt, PresentationGenerationOptions options, CancellationToken cancellationToken = default)
        {
            cancellationCallerOnCall?.Cancel();
            throw new PresentationGenerationStageException(PresentationGenerationStages.DraftParse, GenerationJobErrorCodes.PresentationOutputInvalid, "The presentation response could not be parsed.", LateCompletionUsage);
        }
    }

    private sealed class ScriptedProvider(string draftJson, CancellationTokenSource? cancelCallerBeforeReturn = null) : IPresentationGenerationProvider
    {
        public Task<PresentationProviderResult> GenerateAsync(PresentationGenerationPrompt prompt, PresentationGenerationOptions options, CancellationToken cancellationToken = default)
        {
            var result = new PresentationProviderResult(Deserialize(draftJson), LateCompletionUsage);
            // The response has cleared the provider's own guard; the cancellation is
            // recorded while the handler is still consuming it.
            cancelCallerBeforeReturn?.Cancel();
            return Task.FromResult(result);
        }
    }

    private sealed class PassThroughRenderer : IPresentationRenderer
    {
        private readonly PresentationRenderer inner = new();

        public RenderedPresentation Render(PresentationDraft draft, PresentationGenerationInput input, PresentationGenerationOptions options) => inner.Render(draft, input, options);
    }

    private sealed class ThrowingRenderer : IPresentationRenderer
    {
        public RenderedPresentation Render(PresentationDraft draft, PresentationGenerationInput input, PresentationGenerationOptions options)
            => throw new InvalidOperationException("The presentation renderer could not compose the deck.");
    }

    private sealed class CancellingThrowingRenderer(CancellationTokenSource cancellation) : IPresentationRenderer
    {
        public RenderedPresentation Render(PresentationDraft draft, PresentationGenerationInput input, PresentationGenerationOptions options)
        {
            cancellation.Cancel();
            throw new InvalidOperationException("The presentation renderer could not compose the deck.");
        }
    }

    private sealed class CancellingTruncatedRenderer(CancellationTokenSource cancellation) : IPresentationRenderer
    {
        public RenderedPresentation Render(PresentationDraft draft, PresentationGenerationInput input, PresentationGenerationOptions options)
        {
            cancellation.Cancel();
            return new RenderedPresentation(AssetRepresentationTypes.Pptx, "launch-plan.pptx", "application/octet-stream", new byte[8]);
        }
    }

    private sealed class NullProgress : IProgress<int>
    {
        public void Report(int value) { }
    }
}
