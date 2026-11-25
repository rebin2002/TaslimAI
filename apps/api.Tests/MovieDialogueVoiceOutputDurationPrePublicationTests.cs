using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Test-only negative control for the authored Movie dialogue clip duration gate
/// (<see cref="MovieDialogueVoiceGenerationJobHandler"/> output validation). It pins the one property
/// the boundary tests in <c>MovieDialogueVoiceOutputDurationTests</c> do not assert: the rejection is
/// <em>pre-publication and pre-charge</em>, not merely an exception thrown somewhere in the handler.
/// </summary>
/// <remarks>
/// <para>
/// The worker in <c>GenerationJobExecution</c> performs the duration-independent ordering
/// <c>handler.ExecuteAsync</c> -&gt; <c>publisher.PrepareAsync</c> (stored file + asset) -&gt; completion
/// transaction commit -&gt; take <c>MarkReadyAsync</c> / usage completion. It only ever publishes or settles
/// a charge from the <see cref="GenerationHandlerResult"/> the handler returns: <c>providerUsage</c> is
/// assigned from that result, so a handler that throws leaves the worker with no artifact to publish, no
/// asset or stored-file id to write onto the take, and no usage record to finalise as billable.
/// </para>
/// <para>
/// These tests therefore assert the observable contract the ordering depends on: for an authored window that
/// the request gate legitimately admits (including one exactly at the configured maximum), a non-adapter
/// provider that reports an over-ceiling duration yields <c>no result object at all</c>, while the
/// one-millisecond-lower duration on the same handler does yield a publishable artifact plus chargeable usage.
/// The positive control is what makes the negative assertions non-vacuous: the discriminator between
/// "publish and charge" and "publish nothing" is exactly the duration ceiling.
/// </para>
/// <para>
/// Everything is synthetic and deterministic: no network, no credentials, no live voice or video provider, no
/// database, no charging path, and no dependency on dialogue voice execution being enabled in configuration.
/// The ceiling here is deliberately different from the sibling suite's so that the bound is shown to follow
/// <see cref="MovieDialogueVoiceOptions.MaxDurationMilliseconds"/> rather than any literal in the test.
/// </para>
/// </remarks>
public sealed class MovieDialogueVoiceOutputDurationPrePublicationTests
{
    private const int CeilingMilliseconds = 30_000;
    private const string SyntheticProviderKey = "synthetic-pre-publication";

    /// <summary>
    /// A mock non-adapter provider that reports a duration past the ceiling must be rejected by the output
    /// gate, and the handler must return nothing that the worker could publish as an asset/take or settle as a
    /// charge. The provider call count of one shows the audio really came back and was discarded, rather than
    /// the provider never being reached (which would leave the publication question untested).
    /// </summary>
    [Theory]
    [InlineData(CeilingMilliseconds + 1)]
    [InlineData(CeilingMilliseconds + 2)]
    [InlineData(int.MaxValue)]
    public async Task Over_maximum_dialogue_audio_is_rejected_without_producing_an_asset_or_usage_to_publish(int clipDurationMilliseconds)
    {
        var provider = new FixedDurationDialogueVoiceProvider(clipDurationMilliseconds);

        var (result, error) = await ExecuteAsync(provider, startMilliseconds: 0, endMilliseconds: CeilingMilliseconds);

        Assert.IsType<MovieDialogueVoiceOutputInvalidException>(error);
        Assert.Null(result);
        Assert.Equal(1, provider.InvocationCount);
    }

    /// <summary>
    /// The exact maximum is inclusive and is the last accepted value: at that duration the very same handler
    /// does hand the worker a stored-file artifact, an asset descriptor and a chargeable usage record, which
    /// is precisely why one millisecond more has to leave all three absent.
    /// </summary>
    [Fact]
    public async Task Dialogue_audio_exactly_at_the_maximum_is_handed_over_as_a_publishable_asset_and_chargeable_usage()
    {
        var provider = new FixedDurationDialogueVoiceProvider(CeilingMilliseconds);

        var (result, error) = await ExecuteAsync(provider, startMilliseconds: 0, endMilliseconds: CeilingMilliseconds);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(1, provider.InvocationCount);

        var output = Assert.Single(result!.Outputs);
        Assert.Equal(GenerationJobOutputTypes.StoredFile, output.OutputType);
        Assert.NotNull(output.FileArtifact);
        Assert.NotNull(output.Asset);
        Assert.NotNull(result.Usage);
        Assert.False(result.Usage!.IsTestResponse);
        Assert.Equal(CeilingMilliseconds, ReadDuration(result.ResultJson));
        Assert.Equal(CeilingMilliseconds, ReadDuration(output.MetadataJson!));
    }

    /// <summary>
    /// Both sides of the boundary on a tightened ceiling. The authored window is held at the maximum so the
    /// request gate always admits it and only the output gate can decide the outcome; the accepted rows prove
    /// the gate is not over-tightened, the rejected rows prove it is not loose.
    /// </summary>
    [Theory]
    [InlineData(5_000, 5_000, true)]
    [InlineData(5_000, 5_001, false)]
    [InlineData(CeilingMilliseconds, CeilingMilliseconds, true)]
    [InlineData(CeilingMilliseconds, CeilingMilliseconds + 1, false)]
    public async Task Ceiling_is_inclusive_and_option_driven_on_both_sides_of_the_boundary(int configuredCeiling, int clipDurationMilliseconds, bool expectAccepted)
    {
        var provider = new FixedDurationDialogueVoiceProvider(clipDurationMilliseconds);

        var (result, error) = await ExecuteAsync(provider, startMilliseconds: 0, endMilliseconds: configuredCeiling, maxDurationMilliseconds: configuredCeiling);

        Assert.Equal(1, provider.InvocationCount);
        if (expectAccepted)
        {
            Assert.Null(error);
            Assert.NotNull(result);
            Assert.Equal(clipDurationMilliseconds, ReadDuration(result!.ResultJson));
        }
        else
        {
            Assert.IsType<MovieDialogueVoiceOutputInvalidException>(error);
            Assert.Null(result);
        }
    }

    /// <summary>
    /// Upstream of the provider the same ceiling is enforced by request validation, so an authored window past
    /// the maximum never reaches a provider at all and therefore cannot produce anything to publish or charge.
    /// This is the other half of "always rejected before publication": the gate is not the only line of defence,
    /// and it is deliberately a later one than the request check.
    /// </summary>
    [Theory]
    [InlineData(CeilingMilliseconds + 1)]
    [InlineData(CeilingMilliseconds * 2)]
    public async Task Authored_window_past_the_maximum_is_rejected_before_the_provider_is_invoked(int endMilliseconds)
    {
        var provider = new FixedDurationDialogueVoiceProvider(endMilliseconds);

        var (result, error) = await ExecuteAsync(provider, startMilliseconds: 0, endMilliseconds: endMilliseconds);

        Assert.IsType<MovieDialogueVoiceRequestValidationException>(error);
        Assert.Null(result);
        Assert.Equal(0, provider.InvocationCount);
    }

    private static MovieDialogueVoiceGenerationJobHandler Handler(
        IMovieDialogueVoiceProvider provider,
        int maxDurationMilliseconds = CeilingMilliseconds) => new(
        [provider],
        Options.Create(new MovieDialogueVoiceOptions
        {
            Enabled = true,
            ProviderKey = SyntheticProviderKey,
            MaxDurationMilliseconds = maxDurationMilliseconds,
        }),
        NullLogger<MovieDialogueVoiceGenerationJobHandler>.Instance);

    /// <summary>
    /// Runs the handler exactly as the worker would and captures both possible outcomes: the result it would
    /// publish and charge, or the exception it would map to a failed job.
    /// </summary>
    private static async Task<(GenerationHandlerResult? Result, Exception? Error)> ExecuteAsync(
        IMovieDialogueVoiceProvider provider,
        int startMilliseconds,
        int endMilliseconds,
        int maxDurationMilliseconds = CeilingMilliseconds)
    {
        var handler = Handler(provider, maxDurationMilliseconds);
        try
        {
            return (await handler.ExecuteAsync(
                Job(startMilliseconds, endMilliseconds), new Progress<int>(), CancellationToken.None), null);
        }
        catch (Exception exception) when (exception is MovieDialogueVoiceOutputInvalidException
            or MovieDialogueVoiceRequestValidationException)
        {
            return (null, exception);
        }
    }

    private static GenerationJob Job(int startMilliseconds, int endMilliseconds)
    {
        var input = new MovieDialogueVoiceInput(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Rebin", "The clip is ready for review.",
            MovieDialogueLanguages.English, startMilliseconds, endMilliseconds, null, Guid.NewGuid());
        return new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.MovieDialogueVoiceGenerate,
            InputJson = JsonSerializer.Serialize(input),
        };
    }

    private static int ReadDuration(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("durationMilliseconds").GetInt32();

    /// <summary>Reports a caller-chosen duration and counts how often the handler reached it.</summary>
    private sealed class FixedDurationDialogueVoiceProvider : IMovieDialogueVoiceProvider
    {
        private static readonly byte[] Audio = [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x64];

        public FixedDurationDialogueVoiceProvider(int durationMilliseconds) => DurationMilliseconds = durationMilliseconds;

        public string Key => SyntheticProviderKey;
        public int DurationMilliseconds { get; }
        public int InvocationCount { get; private set; }

        public Task<MovieDialogueVoiceProviderResult> GenerateAsync(
            MovieDialogueVoiceProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return Task.FromResult(new MovieDialogueVoiceProviderResult(
                Audio,
                "audio/mpeg",
                "mp3",
                DurationMilliseconds,
                new MovieDialogueVoiceProviderUsage(
                    "synthetic-dialogue-voice", request.Text.Length, Audio.Length, 0, 0m, 0m,
                    UsageCurrencies.Usd, UsageCostBasis.Actual, "{\"synthetic\":true}")));
        }
    }
}
