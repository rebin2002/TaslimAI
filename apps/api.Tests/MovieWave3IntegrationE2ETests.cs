using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Movies.Wave3Integration;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieWave3SafetyApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MovieVideo:Enabled", "false");
        builder.UseSetting("Billing:CustomerChargingEnabled", "false");
        builder.UseSetting("GenerationJobs:PollIntervalMilliseconds", "25");
    }
}

[CollectionDefinition("MovieWave3Safety", DisableParallelization = true)]
public sealed class MovieWave3SafetyCollectionDefinition { }

[Collection("MovieWave3Safety")]
public sealed class MovieWave3IntegrationE2ETests : IClassFixture<MovieOperationalApiFactory>, IClassFixture<MovieWave3SafetyApiFactory>
{
    private readonly MovieOperationalApiFactory operationalFactory;
    private readonly MovieWave3SafetyApiFactory safetyFactory;

    public MovieWave3IntegrationE2ETests(
        MovieOperationalApiFactory operationalFactory,
        MovieWave3SafetyApiFactory safetyFactory)
    {
        this.operationalFactory = operationalFactory;
        this.safetyFactory = safetyFactory;
        using var operationalScope = operationalFactory.Services.CreateScope();
        operationalScope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var safetyScope = safetyFactory.Services.CreateScope();
        safetyScope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Movie_v2_keeps_multiple_takes_explicit_selection_and_provenance_after_reload()
    {
        using var client = operationalFactory.CreateClient();
        var fixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Wave 3 Selection Acceptance");

        var draftPlan = MovieWave3AdaptiveResolutionCompatibility.Recommend(new MovieWave3AdaptiveResolutionRequest(
            MovieWave3ResolutionContract.P2160,
            MovieQualityLevels.Standard,
            Importance: 20,
            MotionComplexity: 20,
            UpscaleSuitability: 95,
            EconomicalDraft: true));
        Assert.Equal(MovieWave3ResolutionContract.P720, draftPlan.SourceResolution);
        Assert.False(draftPlan.QcEscalationRequired);
        Assert.False(draftPlan.CostEstimate.IsKnown);

        var escalation = MovieWave3AdaptiveResolutionCompatibility.Recommend(new MovieWave3AdaptiveResolutionRequest(
            MovieWave3ResolutionContract.P2160,
            MovieQualityLevels.Standard,
            Importance: 95,
            MotionComplexity: 95,
            CameraComplexity: 90,
            FaceImportance: 95,
            FineDetailImportance: 95,
            ContinuitySensitivity: 90,
            UpscaleSuitability: 0));
        Assert.True(escalation.QcEscalationRequired);
        Assert.NotNull(escalation.EscalateToSourceResolution);

        var provenance = "{\"source\":\"wave3-acceptance\",\"contractVersion\":1}";
        var production = await MovieOperationalFixtures.PostAsync<MovieProductionVersionDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/production/versions", new
            {
                stage = MovieProductionStages.StoryboardCandidate,
                label = "Wave 3 acceptance candidate",
                compositionJson = "{\"composition\":\"subject-left\"}",
                stageProvenanceJson = provenance,
            });
        Assert.Equal(provenance, production.StageProvenanceJson);

        var first = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/takes", new
            {
                label = "Economical draft take",
                qualityLevel = MovieQualityLevels.Standard,
                autoDirectorEnabled = false,
                notes = "Draft remains unselected and is not eligible for premium mastering.",
            });
        var second = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/takes", new
            {
                label = "Selected review take",
                qualityLevel = MovieQualityLevels.Cinematic,
                autoDirectorEnabled = false,
                notes = "Explicitly selected by the acceptance flow.",
            });
        Assert.Equal(1, first.VersionNumber);
        Assert.Equal(2, second.VersionNumber);

        var candidate = MovieWave3UpscaleEligibilityCompatibility.Evaluate(
            new MovieTake { Id = first.Id, Status = MovieTakeStatuses.Draft },
            new MovieShot { Id = fixture.Shot.Id });
        Assert.False(candidate.Eligible);
        Assert.Equal(MovieWave3UpscaleEligibilityCodes.TakeNotSelected, candidate.Code);

        var approved = await MovieOperationalFixtures.PostAsync<MovieV2TakeDto>(client,
            $"/api/movie-studio/takes/{second.Id}/approvals", new
            { decision = MovieApprovalDecisions.Approved, comment = "Selection acceptance approval." });
        Assert.Equal(MovieTakeStatuses.Approved, approved.Status);
        using (var select = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{second.Id}/select", null))
            Assert.Equal(HttpStatusCode.NoContent, select.StatusCode);
        using (var finalize = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post,
                   $"/api/movie-studio/takes/{second.Id}/finalize", null))
            Assert.Equal(HttpStatusCode.NoContent, finalize.StatusCode);

        var reloaded = await MovieOperationalFixtures.GetAsync<MovieV2HierarchyDto>(client,
            $"/api/movie-studio/projects/{fixture.Project.Project.Id}/hierarchy");
        var reloadedShot = reloaded.Acts.Single().Sequences.Single().Scenes.Single().Shots.Single();
        Assert.Equal(second.Id, reloadedShot.SelectedTakeId);
        Assert.Equal(second.Id, reloadedShot.FinalTakeId);
        Assert.Equal(2, reloadedShot.Takes.Count);
        Assert.Equal(MovieTakeStatuses.Draft, reloadedShot.Takes.Single(take => take.Id == first.Id).Status);
        Assert.NotNull(reloadedShot.Takes.Single(take => take.Id == second.Id).FinalizedAt);

        var reloadedProduction = await MovieOperationalFixtures.GetAsync<MovieShotProductionDto>(client,
            $"/api/movie-studio/shots/{fixture.Shot.Id}/production");
        Assert.Contains(reloadedProduction.Versions, version =>
            version.Id == production.Id && version.StageProvenanceJson == provenance);
        var selectedEligibility = MovieWave3UpscaleEligibilityCompatibility.Evaluate(
            new MovieTake { Id = second.Id, Status = MovieTakeStatuses.Approved },
            new MovieShot { Id = fixture.Shot.Id, SelectedTakeId = second.Id, FinalTakeId = second.Id });
        Assert.True(selectedEligibility.Eligible);
        Assert.True(selectedEligibility.IsSelected);
        Assert.True(selectedEligibility.IsFinal);
    }

    [Fact]
    public async Task Provider_disabled_movie_job_fails_without_charge_or_partial_output()
    {
        using var client = safetyFactory.CreateClient();
        var auth = await MovieOperationalFixtures.RegisterAsync(client, "Wave 3 Safety Owner");
        var readiness = await client.GetFromJsonAsync<MovieStudioProviderResponse>("/api/movie-studio/provider");
        Assert.NotNull(readiness);
        Assert.False(readiness!.Provider.Ready);
        Assert.Empty(readiness.Provider.SupportedOperations);

        var now = DateTime.UtcNow;
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        using (var scope = safetyFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var project = new MovieProject
            {
                Id = projectId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                Mode = MovieProjectModes.Quick,
                Status = MovieProjectStatuses.Draft,
                Title = "Wave 3 Disabled Provider",
                Description = "Deterministic provider-disabled failure fixture.",
                DurationSeconds = 5,
                AspectRatio = "16:9",
                Style = "cinematic",
                Language = "en",
                CreatedAt = now,
                UpdatedAt = now,
                Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
            };
            var scene = new MovieScene
            {
                Id = sceneId,
                MovieProjectId = projectId,
                Sequence = 1,
                Title = "Disabled Scene",
                Summary = "A scene used only for the disabled-provider failure path.",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var shot = new MovieShot
            {
                Id = shotId,
                MovieSceneId = sceneId,
                Sequence = 1,
                Description = "A disabled-provider acceptance shot.",
                DurationSeconds = 5,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var clip = new MovieClip
            {
                Id = clipId,
                MovieProjectId = projectId,
                MovieSceneId = sceneId,
                MovieShotId = shotId,
                Status = MovieClipStatuses.Queued,
                DurationSeconds = 5,
                ContinuitySnapshotJson = "{}",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.AddRange(project, scene, shot, clip);
            await db.SaveChangesAsync();
        }

        var input = new MovieGenerationInput(
            MovieStudioOperations.SceneClip,
            projectId,
            clipId,
            sceneId,
            shotId,
            "A deterministic disabled-provider shot.",
            5,
            "16:9",
            "cinematic",
            "en",
            null,
            "{}",
            "{}",
            "{}",
            WorldContextJson: "{}");
        using var response = await MovieOperationalFixtures.SendWithCsrfAsync(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.MovieClipGenerate,
            title = "Disabled provider failure",
            inputJson = JsonSerializer.Serialize(input),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<GenerationJobDto>())!;
        var failed = await WaitForTerminalAsync(client, created.Id);
        Assert.Equal(GenerationJobStatus.Failed.ToString(), failed.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnavailable, failed.ErrorCode);
        Assert.Empty(failed.Outputs);

        using var verifyScope = safetyFactory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await verifyDb.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Id);
        Assert.Equal(UsageFeature.Movie, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.False(await verifyDb.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Id));
        Assert.False(await verifyDb.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Id));

        var summary = await client.GetFromJsonAsync<UsageSummaryDto>($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage/summary");
        Assert.NotNull(summary);
        Assert.Equal(0m, summary!.CustomerChargedAmount);
    }

    private static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            var current = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (current?.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach a terminal state.");
    }
}
