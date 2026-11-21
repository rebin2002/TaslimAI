using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic, provider-free validation of the Movie project export manifest.
/// The manifest is the <see cref="MovieFinalAssemblyInput"/> contract plus the
/// generation job label that carries it through the final-assembly hand-off.
/// Lanes covered: the label bound defect (a project admitted with a
/// maximum-length title produced a 174-character manifest label that no longer
/// fit the persisted job-title column, so the export died at queue admission
/// instead of producing a master), path/name sanitization for every name the
/// manifest emits, and the fail-closed behaviour of a manifest entry whose
/// materialized asset is missing (never a silent placeholder stream).
/// The caption file grammar (#899 lane) and clip idempotency reconciliation
/// (#269 lane) are deliberately out of scope here.
/// </summary>
public sealed class MovieExportManifestSemanticsTests
{
    private static readonly Guid VideoAsset = Guid.NewGuid();
    private static readonly Guid AudioAsset = Guid.NewGuid();
    private static readonly Guid CaptionsAsset = Guid.NewGuid();

    // ------------------------------------------------------------------
    // Lane 1: the manifest export label must fit the persisted name bound
    // ------------------------------------------------------------------

    [Fact]
    public void Maximum_length_project_title_produces_a_manifest_label_inside_the_persisted_job_title_bound()
    {
        // Reproduction. Admission accepts a project title up to the full column
        // bound (160). The manifest label was composed as "{title} master export",
        // i.e. 174 characters, which the database rejects during queue admission,
        // so the customer's export could never start.
        var longestAdmissibleTitle = new string('T', 160);

        var label = MovieExportManifestNaming.ComposeExportLabel(longestAdmissibleTitle);

        Assert.Equal(MovieExportManifestNaming.JobTitleMaxLength, label.Length);
        Assert.EndsWith(MovieExportManifestNaming.ExportSuffix, label, StringComparison.Ordinal);
        Assert.StartsWith("TTTT", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_label_bound_matches_the_persisted_generation_job_title_column()
    {
        // The naming rule is only correct while it agrees with the schema. This
        // reads the real EF model so the bound cannot drift silently.
        var options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseNpgsql("Host=localhost;Database=taslim_model_probe;Username=probe;Password=probe")
            .Options;
        using var context = new TaslimDbContext(options);
        var columnBound = context.Model.FindEntityType(typeof(GenerationJob))!
            .FindProperty(nameof(GenerationJob.Title))!
            .GetMaxLength();

        Assert.Equal(MovieExportManifestNaming.JobTitleMaxLength, columnBound);
    }

    [Fact]
    public void Manifest_label_constants_agree_with_their_literal_sources()
    {
        // The bound is expressed as constants so truncation is a compile-time
        // rule; this test fails the moment the literal and the string drift.
        Assert.Equal(MovieExportManifestNaming.ExportSuffixLength, MovieExportManifestNaming.ExportSuffix.Length);
        Assert.Equal(
            MovieExportManifestNaming.JobTitleMaxLength - MovieExportManifestNaming.ExportSuffix.Length,
            MovieExportManifestNaming.MaximumIntactTitleLength);
    }

    [Fact]
    public void Short_project_title_keeps_the_manifest_label_verbatim_and_deterministic()
    {
        // Idempotent replay depends on a stable label: the same request must
        // always compose the same manifest name.
        Assert.Equal("Quarter Bell master export", MovieExportManifestNaming.ComposeExportLabel("Quarter Bell"));
        Assert.Equal("Quarter Bell master export", MovieExportManifestNaming.ComposeExportLabel("  Quarter Bell  "));
        Assert.Equal(
            MovieExportManifestNaming.ComposeExportLabel(new string('k', 200)),
            MovieExportManifestNaming.ComposeExportLabel(new string('k', 200)));
    }

    [Fact]
    public void Titles_at_the_intact_boundary_are_cut_without_a_single_character_of_the_suffix()
    {
        var intact = new string('a', MovieExportManifestNaming.MaximumIntactTitleLength);
        var oneTooLong = new string('b', MovieExportManifestNaming.MaximumIntactTitleLength + 1);

        Assert.Equal($"{intact}{MovieExportManifestNaming.ExportSuffix}", MovieExportManifestNaming.ComposeExportLabel(intact));
        Assert.Equal($"{oneTooLong[..MovieExportManifestNaming.MaximumIntactTitleLength]}{MovieExportManifestNaming.ExportSuffix}",
            MovieExportManifestNaming.ComposeExportLabel(oneTooLong));
    }

    [Fact]
    public void Truncated_manifest_label_never_splits_a_supplementary_character()
    {
        // The truncation point lands inside an emoji pair unless the cut is
        // character-aware; a lone surrogate would corrupt the label on its way
        // back to the browser.
        var title = string.Concat(new string('c', MovieExportManifestNaming.MaximumIntactTitleLength - 1), "😀", " tail");

        var label = MovieExportManifestNaming.ComposeExportLabel(title);

        Assert.True(label.Length <= MovieExportManifestNaming.JobTitleMaxLength);
        Assert.StartsWith(new string('c', MovieExportManifestNaming.MaximumIntactTitleLength - 1), label, StringComparison.Ordinal);
        Assert.False(label.Any(char.IsHighSurrogate));
        Assert.False(label.Any(char.IsLowSurrogate));
        Assert.DoesNotContain("😀", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_project_title_still_produces_a_meaningful_manifest_label()
    {
        // Legacy or cleared titles must never emit the bare suffix as a name.
        Assert.Equal("Movie master export", MovieExportManifestNaming.ComposeExportLabel(null));
        Assert.Equal("Movie master export", MovieExportManifestNaming.ComposeExportLabel("   "));
        Assert.DoesNotContain("  ", MovieExportManifestNaming.ComposeExportLabel("Film  "), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Lane 2: manifest path and name sanitization
    // ------------------------------------------------------------------

    [Fact]
    public void Export_download_name_collapses_to_a_single_safe_basename_without_path_or_control_leaks()
    {
        // The manifest names the published master through GeneratedMediaSecurity,
        // which sanitizes the artifact file name before it becomes the persisted
        // download name. Windows prefixes, traversal segments, embedded nulls and
        // control characters must never survive onto any host.
        const string adversarial = "C:\\Users\\taslim\\exports\\..\\mas\u0000ter.mp4";

        var descriptor = GeneratedMediaSecurity.ValidateDescriptor(adversarial, "video/mp4");

        Assert.Equal(".mp4", descriptor.Extension);
        Assert.Equal("video/mp4", descriptor.ContentType);
        Assert.Equal("master.mp4", descriptor.SafeFileName);
        Assert.DoesNotContain('\\', descriptor.SafeFileName);
        Assert.DoesNotContain('/', descriptor.SafeFileName);
        Assert.DoesNotContain("..", descriptor.SafeFileName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("master\u0000\r\n.mp4")]
    [InlineData("C:\\windows\\temp\\master.mp4")]
    [InlineData("~/private/master.mp4")]
    [InlineData("..\\..\\master.mp4")]
    [InlineData("mas\u2028ter\u200b.mp4")]
    public void Export_download_names_never_reach_the_browser_with_separators_or_header_breaking_characters(string fileName)
    {
        var safe = FileValidationService.SanitizeFileName(fileName);

        Assert.DoesNotContain('\\', safe);
        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain('\r', safe);
        Assert.DoesNotContain('\n', safe);
        Assert.DoesNotContain('\u0000', safe);
        Assert.DoesNotContain('\u2028', safe);
        Assert.DoesNotContain('\u200b', safe);
        Assert.DoesNotContain("..", safe, StringComparison.Ordinal);
        Assert.EndsWith(".mp4", safe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Materialized_source_names_are_single_segments_pinned_to_the_media_extension_allowlist()
    {
        // The executor materializes each manifest asset as "{assetId:N}{extension}"
        // inside a per-job directory. Only lowercase, single-dot media extensions
        // are issuable by the generated-media gate, so no validated extension can
        // escape the directory; traversal-shaped names fail closed at the gate
        // before a StoredFile row can ever reach the manifest.
        foreach (var extension in new[] { ".mp4", ".webm", ".mov", ".wav", ".mp3" })
        {
            var name = $"{Guid.NewGuid():N}{extension}";
            Assert.DoesNotContain('\\', name);
            Assert.DoesNotContain('/', name);
            var full = Path.Combine("/tmp/taslim-export-manifest-test", name);
            Assert.Equal("/tmp/taslim-export-manifest-test", Path.GetDirectoryName(full));
        }

        Assert.Throws<FileUploadValidationException>(
            () => GeneratedMediaSecurity.ValidateDescriptor("../mp4", "video/mp4"));

        // An empty basename collapses to the placeholder identity rather than a
        // bare extension, and an extension-only file name keeps no extension at
        // all after sanitization, so the manifest can never persist ".mp4".
        Assert.Equal("uploaded-file", FileValidationService.SanitizeFileName("..."));
        Assert.Throws<FileUploadValidationException>(
            () => GeneratedMediaSecurity.ValidateDescriptor("...", "video/mp4"));
    }

    [Fact]
    public void Manifest_argument_names_reach_ffmpeg_only_as_discrete_arguments_without_shell_interpolation()
    {
        // Even when a materialized path carries shell metacharacters, the builder
        // must emit it verbatim as one discrete argv entry, never split one
        // manifest name across arguments and never duplicate an input.
        var manifest = Manifest(MovieAssemblyCaptionModes.None);
        var videoPath = "/tmp/taslim/-i ; touch $(echo pwned).mp4";
        var audioPath = "/tmp/taslim/|amp;.wav";

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            Probed(manifest, new Dictionary<Guid, string>
            {
                [VideoAsset] = videoPath,
                [AudioAsset] = audioPath,
            }),
            "/tmp/taslim/out dir/master.mp4");

        Assert.Equal(2, arguments.Count(item => item == "-i"));
        Assert.Single(arguments, item => item == videoPath);
        Assert.Single(arguments, item => item == audioPath);
        Assert.Single(arguments, item => item == "/tmp/taslim/out dir/master.mp4");
        Assert.DoesNotContain(arguments, item => item.Contains("sh -c", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, item => item == "$(echo pwned).mp4");
        Assert.DoesNotContain(arguments, item => item == "; touch $(echo pwned).mp4");
    }

    [Fact]
    public void Adversarial_profile_names_are_never_interpreted_as_output_paths_or_vendor_controls()
    {
        // The manifest carries the resolution profile as a bounded vocabulary key.
        // A traversal-shaped or vendor-shaped profile key fails closed at the gate.
        Assert.False(MovieFinalAssemblyProfiles.TryGet("../../etc/passwd", out _));
        Assert.False(MovieFinalAssemblyProfiles.TryGet("vendor:model;rm", out _));
        Assert.False(MovieFinalAssemblyProfiles.TryGet(null, out _));
        Assert.True(MovieFinalAssemblyProfiles.TryGet(" UHD-4K ", out var profile));
        Assert.Equal(3_840, profile.Width);
    }

    [Fact]
    public void Serialized_manifest_names_sources_by_id_and_never_carries_paths_or_provider_controls()
    {
        // The export manifest is persisted inside the generation job payload. It
        // must describe sources by immutable ids and product vocabulary only,
        // because the executor re-materializes names at run time.
        var manifest = Manifest(MovieAssemblyCaptionModes.Embedded);
        var json = JsonSerializer.Serialize(manifest);

        Assert.Contains(VideoAsset.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AudioAsset.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CaptionsAsset.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/tmp/", json, StringComparison.Ordinal);
        Assert.DoesNotContain(".mp4", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Operation\":\"final_assembly\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_output_name_is_the_constant_master_artifact_never_a_title_derived_value()
    {
        // The download identity of the export is fixed by the executor, so a
        // user-controlled project title can never influence the emitted file
        // name; the title only labels the job.
        var manifest = Manifest(MovieAssemblyCaptionModes.None);
        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            Probed(manifest, new Dictionary<Guid, string>
            {
                [VideoAsset] = "/tmp/taslim/video.mp4",
                [AudioAsset] = "/tmp/taslim/audio.wav",
            }),
            "/tmp/taslim-final-assembly-9f2c/master.mp4");

        Assert.Equal("master.mp4", Path.GetFileName(arguments[^1]));
        Assert.DoesNotContain("title", JsonSerializer.Serialize(manifest), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Lane 3: a missing manifest asset fails closed, never a placeholder
    // ------------------------------------------------------------------

    [Fact]
    public void Manifest_missing_timeline_asset_fails_closed_instead_of_becoming_a_silent_placeholder()
    {
        var manifest = Manifest(MovieAssemblyCaptionModes.None);
        var audioOnly = new Dictionary<Guid, string> { [AudioAsset] = "/tmp/taslim/audio.wav" };

        var error = Assert.Throws<MovieFinalAssemblyExecutionException>(
            () => FfmpegMovieFinalAssemblyCommandBuilder.Build(
                new MovieFinalAssemblyExecutionRequest(manifest, audioOnly), "/tmp/out.mp4"));

        Assert.Equal("ASSEMBLY_SOURCE_PATH_MISSING", error.Code);
    }

    [Fact]
    public void Manifest_missing_audio_asset_fails_closed_instead_of_becoming_a_silent_placeholder()
    {
        var manifest = Manifest(MovieAssemblyCaptionModes.None);
        var videoOnly = new Dictionary<Guid, string> { [VideoAsset] = "/tmp/taslim/video.mp4" };

        var error = Assert.Throws<MovieFinalAssemblyExecutionException>(
            () => FfmpegMovieFinalAssemblyCommandBuilder.Build(
                new MovieFinalAssemblyExecutionRequest(manifest, videoOnly), "/tmp/out.mp4"));

        Assert.Equal("ASSEMBLY_AUDIO_PATH_MISSING", error.Code);
    }

    [Theory]
    [InlineData(MovieAssemblyCaptionModes.BurnIn)]
    [InlineData(MovieAssemblyCaptionModes.Embedded)]
    public void Manifest_missing_caption_asset_fails_closed_for_every_captioned_mode(string mode)
    {
        var manifest = Manifest(mode);
        var withoutCaptions = new Dictionary<Guid, string>
        {
            [VideoAsset] = "/tmp/taslim/video.mp4",
            [AudioAsset] = "/tmp/taslim/audio.wav",
        };

        var error = Assert.Throws<MovieFinalAssemblyExecutionException>(
            () => FfmpegMovieFinalAssemblyCommandBuilder.Build(
                Probed(manifest, withoutCaptions), "/tmp/out.mp4"));

        Assert.Equal("ASSEMBLY_CAPTIONS_PATH_MISSING", error.Code);
    }

    [Fact]
    public void Manifest_reuses_one_materialized_source_for_repeated_references_without_placeholder_streams()
    {
        // Materialization deduplicates manifest assets by id, so one shared path
        // entry must satisfy every reference exactly: one -i per manifest input
        // slot and no phantom placeholder input for the repeat.
        var manifest = Manifest(MovieAssemblyCaptionModes.None) with
        {
            Timeline =
            [
                new MovieAssemblyTimelineItem(Guid.NewGuid(), VideoAsset, 0, 0m, 2.5m, "hash-a"),
                new MovieAssemblyTimelineItem(Guid.NewGuid(), VideoAsset, 1, 0m, 2.0m, "hash-b"),
            ],
        };
        var paths = new Dictionary<Guid, string>
        {
            [VideoAsset] = "/tmp/taslim/video.mp4",
            [AudioAsset] = "/tmp/taslim/audio.wav",
        };

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            Probed(manifest, paths), "/tmp/out.mp4");

        Assert.Equal(3, arguments.Count(item => item == "-i"));
        Assert.Equal(2, arguments.Count(item => item == "/tmp/taslim/video.mp4"));
        Assert.Contains("concat=n=2:v=1:a=1", arguments.Single(item => item.Contains("concat=", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_caption_stream_is_mapped_from_its_manifest_position_after_all_declared_inputs()
    {
        // The embedded track is materialized like every other manifest asset and
        // appended after the video and audio inputs; its stream index must match
        // the manifest position (timeline + audio) or ffmpeg would map a
        // placeholder stream.
        var manifest = Manifest(MovieAssemblyCaptionModes.Embedded);
        var paths = new Dictionary<Guid, string>
        {
            [VideoAsset] = "/tmp/taslim/video.mp4",
            [AudioAsset] = "/tmp/taslim/audio.wav",
            [CaptionsAsset] = "/tmp/taslim/captions.vtt",
        };

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            Probed(manifest, paths), "/tmp/out.mp4");

        var inputPositions = new List<int>();
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] == "-i") inputPositions.Add(index);
        }
        Assert.Equal(3, inputPositions.Count);
        Assert.Equal("/tmp/taslim/captions.vtt", arguments[inputPositions[^1] + 1]);
        Assert.Contains("2:0", arguments);
        Assert.Contains("-c:s", arguments);
        Assert.Contains("mov_text", arguments);
    }

    private static MovieFinalAssemblyInput Manifest(string captionsMode)
    {
        var profile = new MovieFinalAssemblyProfile(MovieFinalAssemblyProfiles.Uhd4K, 3_840, 2_160);
        return new MovieFinalAssemblyInput(
            "final_assembly",
            Guid.NewGuid(),
            Guid.NewGuid(),
            profile.Key,
            profile,
            [new MovieAssemblyTimelineItem(Guid.NewGuid(), VideoAsset, 0, 0m, 2.5m, "continuity-hash")],
            [new MovieAssemblyAudioMixInput(AudioAsset, "music", -3m, null, null, true)],
            new MovieAssemblyCaptions(captionsMode, captionsMode == MovieAssemblyCaptionModes.None ? null : CaptionsAsset, "en"),
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "assembly:manifest-semantics",
            3);
    }

    // The builder refuses to emit any [i:v]/[i:a] reference without probe evidence
    // for every distinct timeline source (MovieFinalAssemblyExecutionRequest.VideoProbes),
    // because only the real executor probes each input before rendering. A manifest
    // semantics test therefore has to supply that evidence to reach the behaviour it
    // asserts. The fail-closed lanes above deliberately omit it: a missing source path
    // is rejected before the probe gate is ever reached.
    private static MovieFinalAssemblyExecutionRequest Probed(
        MovieFinalAssemblyInput manifest,
        IReadOnlyDictionary<Guid, string> paths) =>
        new(manifest, paths, manifest.Timeline
            .Select(item => item.AssetId)
            .Distinct()
            .ToDictionary(id => id, _ => new MovieFinalAssemblyVideoProbe(600m, true)));
}

/// <summary>
/// The same defect observed through the real API, persistence, generation worker
/// and asset-publication stack. Only the video provider and the FFmpeg executor
/// are deterministic in-memory doubles, reused from the accepted final-assembly
/// acceptance harness. Before the fix, the queue request for a project admitted
/// with a maximum-length title wrote a 174-character manifest label; production
/// PostgreSQL rejects that row and the assembly is stranded as Failed with
/// MOVIE_ASSEMBLY_QUEUE_FAILED, so the export never runs.
/// </summary>
[Collection("MovieFinalAssemblyAcceptance")]
public sealed class MovieExportManifestLabelApiTests : IClassFixture<MovieFinalAssemblyApiFactory>
{
    private readonly MovieFinalAssemblyApiFactory factory;

    public MovieExportManifestLabelApiTests(MovieFinalAssemblyApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Export_of_a_maximum_length_title_project_queues_with_a_bounded_manifest_label_and_downloads()
    {
        using var client = factory.CreateClient();
        factory.Scenarios[FakeProviderKind.Movie] = FakeProviderScenario.ImmediateSuccess;
        var longTitle = new string('T', 160);
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: longTitle);
        var projectId = fixture.Project.Project.Id;
        var takeId = await CreateFinalizedTakeAsync(client, projectId, fixture.Shot.Id, "manifest-label-bound");

        var assembly = await MovieOperationalFixtures.PostAsync<MovieFinalAssemblyDto>(
            client,
            $"/api/movie-studio/projects/{projectId}/final-assembly",
            new
            {
                resolutionProfile = MovieFinalAssemblyProfiles.Hd1080p,
                timeline = new[] { new { takeId, inPointSeconds = 0m } },
            },
            idempotencyKey: "manifest-label-bound-1");

        Assert.Equal(MovieAssemblyStatuses.Queued, assembly.Status);
        Assert.NotNull(assembly.GenerationJobId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = await db.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == assembly.GenerationJobId!.Value);
            Assert.NotNull(job.Title);
            Assert.True(job.Title!.Length <= MovieExportManifestNaming.JobTitleMaxLength,
                $"The manifest export label was persisted at {job.Title.Length} characters; the job-title column bound is {MovieExportManifestNaming.JobTitleMaxLength}.");
            Assert.EndsWith(MovieExportManifestNaming.ExportSuffix, job.Title, StringComparison.Ordinal);
            Assert.Equal(MovieExportManifestNaming.ComposeExportLabel(longTitle), job.Title);
        }

        var ready = await WaitForTerminalAssemblyAsync(client, assembly.Id);
        Assert.Equal(MovieAssemblyStatuses.Ready, ready.Status);

        using var download = await client.GetAsync($"/api/movie-studio/final-assemblies/{assembly.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("video/mp4", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("master.mp4", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    private async Task<Guid> CreateFinalizedTakeAsync(HttpClient client, Guid projectId, Guid shotId, string keyPrefix)
    {
        var generated = await MovieOperationalFixtures.PostAsync<MovieStudioGenerationResponse>(
            client,
            $"/api/movie-studio/shots/{shotId}/generate",
            new { title = "Manifest label source take" },
            idempotencyKey: $"{keyPrefix}-generate");
        var terminal = await WaitForTerminalJobAsync(client, generated.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), terminal.Status);

        var assetId = await WaitForClipAssetAsync(generated.ClipId);
        var take = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(
            client,
            $"/api/movie-studio/shots/{shotId}/takes",
            new
            {
                label = "Manifest label raw take",
                qualityLevel = MovieQualityLevels.Standard,
                autoDirectorEnabled = false,
                movieClipId = generated.ClipId,
                generationJobId = generated.Job.Id,
                assetId,
                notes = "Raw footage candidate retained for the export manifest label acceptance.",
            });
        await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(
            client,
            $"/api/movie-studio/takes/{take.Id}/approvals",
            new { decision = MovieApprovalDecisions.Approved, comment = "Reviewable take." });

        using var select = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
            $"/api/movie-studio/takes/{take.Id}/select", null);
        Assert.Equal(HttpStatusCode.NoContent, select.StatusCode);
        using var finalize = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
            $"/api/movie-studio/takes/{take.Id}/finalize", null);
        Assert.Equal(HttpStatusCode.NoContent, finalize.StatusCode);
        return take.Id;
    }

    private async Task<Guid> WaitForClipAssetAsync(Guid clipId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        for (var attempt = 0; attempt < 160; attempt++)
        {
            var assetId = await db.MovieClips.AsNoTracking()
                .Where(item => item.Id == clipId)
                .Select(item => item.AssetId)
                .FirstOrDefaultAsync();
            if (assetId.HasValue) return assetId.Value;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Movie clip {clipId} never received a private asset.");
    }

    private static async Task<GenerationJobDto> WaitForTerminalJobAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var current = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (current?.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }

    private static async Task<MovieFinalAssemblyDto> WaitForTerminalAssemblyAsync(HttpClient client, Guid assemblyId)
    {
        MovieFinalAssemblyDto? current = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            current = await client.GetFromJsonAsync<MovieFinalAssemblyDto>($"/api/movie-studio/final-assemblies/{assemblyId}");
            if (current?.Status is MovieAssemblyStatuses.Ready or MovieAssemblyStatuses.Failed or MovieAssemblyStatuses.Cancelled) return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Final assembly {assemblyId} did not reach a terminal state. Last status: {current?.Status}.");
    }
}

/// <summary>
/// The production consequence of the same defect, observed against the disposable
/// PostgreSQL the API CI job provides. The repository's SQLite suites store an
/// over-long job title silently and stay green on the broken composition, so the
/// durability half of this contract can only be proven on the provider that
/// actually enforces the column bound.
/// </summary>
public sealed class MovieExportManifestLabelPersistenceTests : IAsyncLifetime
{
    private readonly string schema = $"movie_manifest_{Guid.NewGuid():N}";
    private DbContextOptions<TaslimDbContext> options = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString)
            .Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            await using var admin = new NpgsqlConnection(connectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }

    [DisposablePostgresFact]
    public async Task The_unbounded_manifest_label_is_refused_by_the_job_title_column()
    {
        // The pre-fix composition: a project title admitted at the full bound plus
        // the delivery suffix. PostgreSQL rejects the row, which is exactly the
        // failure that stranded the assembly as a queue failure for the customer.
        var composedBeforeFix = $"{new string('T', 160)}{MovieExportManifestNaming.ExportSuffix}";
        await using var db = await SeedAsync();
        var job = Job(composedBeforeFix);
        db.GenerationJobs.Add(job);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Contains("value too long", error.InnerException?.Message ?? error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [DisposablePostgresFact]
    public async Task The_bounded_manifest_label_persists_and_reads_back_whole()
    {
        var longTitle = string.Concat(new string('T', 158), "🙂");
        var label = MovieExportManifestNaming.ComposeExportLabel(longTitle);
        await using var db = await SeedAsync();
        db.GenerationJobs.Add(Job(label));
        await db.SaveChangesAsync();

        await using var verify = new TaslimDbContext(options);
        var stored = await verify.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == currentJobId);

        Assert.Equal(label, stored.Title);
        Assert.Equal(MovieExportManifestNaming.JobTitleMaxLength, stored.Title!.Length);
        Assert.DoesNotContain("🙂", stored.Title, StringComparison.Ordinal);
        for (var index = 0; index < stored.Title!.Length; index++)
        {
            if (char.IsHighSurrogate(stored.Title[index]))
            {
                Assert.True(index + 1 < stored.Title.Length && char.IsLowSurrogate(stored.Title[index + 1]),
                    $"Code unit {index} is a high surrogate with no low surrogate after it.");
                index++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(stored.Title[index]), $"Code unit {index} is an orphaned low surrogate.");
            }
        }
    }

    private Guid currentJobId;

    private GenerationJob Job(string title)
    {
        currentJobId = Guid.NewGuid();
        return new GenerationJob
        {
            Id = currentJobId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            JobType = GenerationJobTypes.MovieAssembly,
            Status = GenerationJobStatus.Pending,
            Title = title,
            InputJson = "{}",
            CreatedAt = DateTime.UtcNow,
        };
    }

    private Guid workspaceId;
    private Guid userId;

    private async Task<TaslimDbContext> SeedAsync()
    {
        var db = new TaslimDbContext(options);
        var now = DateTime.UtcNow;
        var seed = Guid.NewGuid().ToString("N");
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Movie export manifest label",
            Slug = $"movie-manifest-{seed}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"movie-manifest-{seed}@example.com",
            Email = $"movie-manifest-{seed}@example.com",
            DisplayName = "Movie export manifest tester",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            JoinedAt = now,
        });
        await db.SaveChangesAsync();
        workspaceId = workspace.Id;
        userId = user.Id;
        return db;
    }
}
