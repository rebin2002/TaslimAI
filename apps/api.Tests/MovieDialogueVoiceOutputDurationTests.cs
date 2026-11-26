using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Bounded contract tests for the duration ceiling on authored Movie dialogue audio clips.
/// The authored timing window is already bounded by
/// <see cref="MovieDialogueVoiceOptions.MaxDurationMilliseconds"/> at both the production
/// service boundary and the provider adapter, and the adapter re-bounds the duration it
/// receives. The execution handler is the last gate before a duration is written into take
/// metadata, the job result payload, and <c>MovieDialogueTake.DurationMilliseconds</c>, so it
/// must apply the same ceiling to whatever duration the selected provider returns.
/// Everything here is synthetic: no provider credentials, no network, no real user data, and no
/// dependency on a provider or charging path being enabled.
/// </summary>
public sealed class MovieDialogueVoiceOutputDurationTests
{
    private const int CeilingMilliseconds = 900_000;
    private const string SyntheticProviderKey = "synthetic-fixed-duration";

    /// <summary>
    /// Reproduces the defect: the handler accepted a clip whose duration exceeded the ceiling it
    /// enforces on the authored window, because the output gate only rejected non-positive values.
    /// </summary>
    [Theory]
    [InlineData(CeilingMilliseconds + 1)]
    [InlineData(10_000_000)]
    [InlineData(int.MaxValue)]
    public async Task Handler_rejects_dialogue_audio_longer_than_the_configured_ceiling(int clipDurationMilliseconds)
    {
        var handler = Handler(new FixedDurationDialogueVoiceProvider(clipDurationMilliseconds));

        await Assert.ThrowsAsync<MovieDialogueVoiceOutputInvalidException>(
            () => handler.ExecuteAsync(Job(startMilliseconds: 0, endMilliseconds: 1_000), new Progress<int>(), CancellationToken.None));
    }

    /// <summary>
    /// The ceiling is inclusive: a clip of exactly the configured maximum is a legitimate result
    /// and must not be rejected by an over-tightened comparison.
    /// </summary>
    [Fact]
    public async Task Handler_accepts_dialogue_audio_exactly_at_the_ceiling()
    {
        var handler = Handler(new FixedDurationDialogueVoiceProvider(CeilingMilliseconds));

        var result = await handler.ExecuteAsync(
            Job(startMilliseconds: 0, endMilliseconds: CeilingMilliseconds), new Progress<int>(), CancellationToken.None);

        Assert.Equal(CeilingMilliseconds, ReadDuration(result.ResultJson));
        Assert.Equal(CeilingMilliseconds, ReadDuration(result.Outputs[0].MetadataJson!));
    }

    /// <summary>
    /// The bound must track the configured option rather than the historical literal, so an
    /// operator who tightens the ceiling also tightens accepted clip durations.
    /// </summary>
    [Fact]
    public async Task Handler_bounds_clip_duration_to_the_configured_option_not_a_literal()
    {
        var handler = Handler(new FixedDurationDialogueVoiceProvider(5_000), maxDurationMilliseconds: 2_000);

        await Assert.ThrowsAsync<MovieDialogueVoiceOutputInvalidException>(
            () => handler.ExecuteAsync(Job(startMilliseconds: 0, endMilliseconds: 1_000), new Progress<int>(), CancellationToken.None));
    }

    /// <summary>
    /// A non-positive ceiling is normalised to one millisecond everywhere else in this feature;
    /// the output gate must fail closed the same way instead of accepting an arbitrary duration.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Handler_fails_closed_when_the_configured_ceiling_is_not_positive(int configuredCeiling)
    {
        var handler = Handler(new FixedDurationDialogueVoiceProvider(1_000), maxDurationMilliseconds: configuredCeiling);

        await Assert.ThrowsAsync<MovieDialogueVoiceOutputInvalidException>(
            () => handler.ExecuteAsync(Job(startMilliseconds: 0, endMilliseconds: 1), new Progress<int>(), CancellationToken.None));
    }

    /// <summary>Pins the pre-existing lower bound so the new upper bound cannot displace it.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Handler_still_rejects_non_positive_dialogue_audio_duration(int clipDurationMilliseconds)
    {
        var handler = Handler(new FixedDurationDialogueVoiceProvider(clipDurationMilliseconds));

        await Assert.ThrowsAsync<MovieDialogueVoiceOutputInvalidException>(
            () => handler.ExecuteAsync(Job(startMilliseconds: 0, endMilliseconds: 1_000), new Progress<int>(), CancellationToken.None));
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

    private static GenerationJob Job(int startMilliseconds, int endMilliseconds)
    {
        var input = new MovieDialogueVoiceInput(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Mara", "The handoff is ready.",
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

    /// <summary>Returns a caller-chosen duration so the ceiling can be probed at exact boundaries.</summary>
    private sealed class FixedDurationDialogueVoiceProvider : IMovieDialogueVoiceProvider
    {
        private static readonly byte[] Audio = [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x64];

        public FixedDurationDialogueVoiceProvider(int durationMilliseconds) => DurationMilliseconds = durationMilliseconds;

        public string Key => SyntheticProviderKey;
        public int DurationMilliseconds { get; }

        public Task<MovieDialogueVoiceProviderResult> GenerateAsync(
            MovieDialogueVoiceProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
