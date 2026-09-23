using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieStudioService
{
    Task<MovieStudioProjectResponse?> CreateAsync(Guid userId, MovieStudioCreateRequest request, CancellationToken cancellationToken);
    Task<MovieStudioProjectDto?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);
    Task<MovieStudioProjectDto?> UpdateGuideAsync(Guid userId, Guid id, MovieStudioGuideRequest request, CancellationToken cancellationToken);
    Task<MovieSceneDto?> AddSceneAsync(Guid userId, Guid id, MovieStudioSceneRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterDto?> AddCharacterAsync(Guid userId, Guid id, MovieStudioCharacterRequest request, CancellationToken cancellationToken);
    Task<MovieLocationDto?> AddLocationAsync(Guid userId, Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken);
    Task<MovieShotDto?> AddShotAsync(Guid userId, Guid sceneId, MovieStudioShotRequest request, CancellationToken cancellationToken);
    Task<MovieProviderReadinessDto> ProviderReadinessAsync();
}

public sealed class MovieStudioService(TaslimDbContext db, WorkspaceAccessService access, IGenerationJobService jobs, IMovieVideoProvider provider) : IMovieStudioService
{
    public async Task<MovieStudioProjectResponse?> CreateAsync(Guid userId, MovieStudioCreateRequest request, CancellationToken cancellationToken)
    {
        var validation = MovieStudioValidation.Validate(request);
        if (validation is not null) throw new MovieStudioValidationException(validation);
        if (!await access.IsMemberAsync(userId, request.WorkspaceId, cancellationToken)) return null;
        if (request.ProjectId.HasValue && !await db.Projects.AnyAsync(project => project.Id == request.ProjectId && project.WorkspaceId == request.WorkspaceId, cancellationToken))
            throw new MovieStudioValidationException("The selected project is not in this workspace.");

        var now = DateTime.UtcNow;
        Guid? projectId = request.ProjectId;
        if (request.Mode == MovieProjectModes.Full && !projectId.HasValue)
        {
            var rootProject = new Project
            {
                Id = Guid.NewGuid(), WorkspaceId = request.WorkspaceId, Name = request.Title.Trim(),
                Description = request.Description.Trim(), Type = ProjectTypes.Movie, Status = ProjectStatuses.Active,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Projects.Add(rootProject);
            projectId = rootProject.Id;
        }

        var movie = new MovieProject
        {
            Id = Guid.NewGuid(), WorkspaceId = request.WorkspaceId, ProjectId = projectId, CreatedByUserId = userId,
            Mode = request.Mode, Status = MovieProjectStatuses.Draft, Title = request.Title.Trim(), Description = request.Description.Trim(),
            DurationSeconds = request.DurationSeconds, AspectRatio = request.AspectRatio, Style = request.Style.Trim(),
            Language = request.Language.ToLowerInvariant(), AdditionalInstructions = MovieStudioHelpers.Clean(request.AdditionalInstructions),
            CreatedAt = now, UpdatedAt = now,
            Guide = new MovieContinuityGuide
            {
                Id = Guid.NewGuid(), VisualLanguage = request.VisualLanguage?.Trim() ?? string.Empty,
                CameraLanguage = request.CameraLanguage?.Trim() ?? string.Empty, ColorAndLighting = request.ColorAndLighting?.Trim() ?? string.Empty,
                SoundAndNarration = request.SoundAndNarration?.Trim() ?? string.Empty, ContinuityRules = request.ContinuityRules?.Trim() ?? string.Empty,
                UpdatedAt = now,
            },
        };
        db.MovieProjects.Add(movie);
        await db.SaveChangesAsync(cancellationToken);

        GenerationJobDto? job = null;
        if (request.Mode == MovieProjectModes.Quick)
        {
            var createdJob = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
            {
                WorkspaceId = request.WorkspaceId, ProjectId = projectId, JobType = GenerationJobTypes.MovieQuickGenerate,
                Title = request.Title.Trim(), InputJson = JsonSerializer.Serialize(new MovieGenerationInput(
                    MovieStudioOperations.QuickMovie, movie.Description, movie.DurationSeconds, movie.AspectRatio,
                    movie.Style, movie.Language, movie.AdditionalInstructions, null, null)),
            }, cancellationToken);
            job = GenerationJobContractMapper.ToDto(createdJob);
        }

        var saved = await GetAsync(userId, movie.Id, cancellationToken);
        return saved is null ? null : new MovieStudioProjectResponse(saved, job);
    }

    public async Task<MovieStudioProjectDto?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var movie = await Query().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken) ? null : ToDto(movie);
    }

    public async Task<MovieStudioProjectDto?> UpdateGuideAsync(Guid userId, Guid id, MovieStudioGuideRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.Include(item => item.Guide).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        movie.Guide.VisualLanguage = request.VisualLanguage?.Trim() ?? string.Empty;
        movie.Guide.CameraLanguage = request.CameraLanguage?.Trim() ?? string.Empty;
        movie.Guide.ColorAndLighting = request.ColorAndLighting?.Trim() ?? string.Empty;
        movie.Guide.SoundAndNarration = request.SoundAndNarration?.Trim() ?? string.Empty;
        movie.Guide.ContinuityRules = request.ContinuityRules?.Trim() ?? string.Empty;
        movie.Guide.UpdatedAt = movie.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(userId, id, cancellationToken);
    }

    public async Task<MovieSceneDto?> AddSceneAsync(Guid userId, Guid id, MovieStudioSceneRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Summary)) throw new MovieStudioValidationException("Scene title and summary are required.");
        var now = DateTime.UtcNow;
        var scene = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = id, Sequence = await db.MovieScenes.CountAsync(item => item.MovieProjectId == id, cancellationToken) + 1, Title = request.Title.Trim(), Summary = request.Summary.Trim(), DurationSeconds = request.DurationSeconds, ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), Narration = MovieStudioHelpers.Clean(request.Narration), Dialogue = MovieStudioHelpers.Clean(request.Dialogue), CreatedAt = now, UpdatedAt = now };
        db.MovieScenes.Add(scene);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(scene, []);
    }

    public async Task<MovieCharacterDto?> AddCharacterAsync(Guid userId, Guid id, MovieStudioCharacterRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Character name and description are required.");
        var now = DateTime.UtcNow;
        var character = new MovieCharacter { Id = Guid.NewGuid(), MovieProjectId = id, Name = request.Name.Trim(), Description = request.Description.Trim(), Appearance = MovieStudioHelpers.Clean(request.Appearance), VoiceAndPerformance = MovieStudioHelpers.Clean(request.VoiceAndPerformance), ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieCharacters.Add(character);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(character);
    }

    public async Task<MovieLocationDto?> AddLocationAsync(Guid userId, Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Location name and description are required.");
        var now = DateTime.UtcNow;
        var location = new MovieLocation { Id = Guid.NewGuid(), MovieProjectId = id, Name = request.Name.Trim(), Description = request.Description.Trim(), VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieLocations.Add(location);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(location);
    }

    public async Task<MovieShotDto?> AddShotAsync(Guid userId, Guid sceneId, MovieStudioShotRequest request, CancellationToken cancellationToken)
    {
        var scene = await db.MovieScenes.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == sceneId, cancellationToken);
        if (scene is null || !await access.IsMemberAsync(userId, scene.MovieProject.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Shot description is required.");
        var now = DateTime.UtcNow;
        var shot = new MovieShot { Id = Guid.NewGuid(), MovieSceneId = sceneId, Sequence = await db.MovieShots.CountAsync(item => item.MovieSceneId == sceneId, cancellationToken) + 1, Description = request.Description.Trim(), CameraAndFraming = MovieStudioHelpers.Clean(request.CameraAndFraming), CameraMotion = MovieStudioHelpers.Clean(request.CameraMotion), DurationSeconds = request.DurationSeconds, Narration = MovieStudioHelpers.Clean(request.Narration), Dialogue = MovieStudioHelpers.Clean(request.Dialogue), VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes), CreatedAt = now, UpdatedAt = now };
        db.MovieShots.Add(shot);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(shot, []);
    }

    public Task<MovieProviderReadinessDto> ProviderReadinessAsync() => Task.FromResult(new MovieProviderReadinessDto(provider.IsAvailable, provider.IsAvailable ? provider.Key : null, [MovieStudioOperations.QuickMovie, MovieStudioOperations.SceneClip, MovieStudioOperations.Assembly]));

    private IQueryable<MovieProject> Query() => db.MovieProjects.AsNoTracking().Include(item => item.Guide).Include(item => item.Scenes).ThenInclude(scene => scene.Shots).ThenInclude(shot => shot.Clips).Include(item => item.Characters).Include(item => item.Locations).Include(item => item.Assemblies);

    private static MovieStudioProjectDto ToDto(MovieProject movie) => new(movie.Id, movie.WorkspaceId, movie.ProjectId, movie.Mode, movie.Status, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language, movie.AdditionalInstructions, movie.CreatedAt, movie.UpdatedAt, new MovieGuideDto(movie.Guide.Id, movie.Guide.VisualLanguage, movie.Guide.CameraLanguage, movie.Guide.ColorAndLighting, movie.Guide.SoundAndNarration, movie.Guide.ContinuityRules, movie.Guide.UpdatedAt), movie.Scenes.OrderBy(scene => scene.Sequence).Select(scene => ToDto(scene, scene.Shots.OrderBy(shot => shot.Sequence).Select(shot => ToDto(shot, shot.Clips.Select(ToDto).ToArray())).ToArray())).ToArray(), movie.Characters.OrderBy(character => character.CreatedAt).Select(ToDto).ToArray(), movie.Locations.OrderBy(location => location.CreatedAt).Select(ToDto).ToArray(), movie.Assemblies.OrderByDescending(assembly => assembly.CreatedAt).Select(ToDto).ToArray());
    private static MovieSceneDto ToDto(MovieScene scene, IReadOnlyList<MovieShotDto> shots) => new(scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes, scene.Narration, scene.Dialogue, shots);
    private static MovieShotDto ToDto(MovieShot shot, IReadOnlyList<MovieClipDto> clips) => new(shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes, clips);
    private static MovieCharacterDto ToDto(MovieCharacter character) => new(character.Id, character.Name, character.Description, character.Appearance, character.VoiceAndPerformance, character.ContinuityNotes, character.ReferenceAssetId);
    private static MovieLocationDto ToDto(MovieLocation location) => new(location.Id, location.Name, location.Description, location.VisualContinuityNotes, location.ReferenceAssetId);
    private static MovieClipDto ToDto(MovieClip clip) => new(clip.Id, clip.MovieShotId, clip.GenerationJobId, clip.AssetId, clip.Status, clip.ProviderKey, clip.DurationSeconds, clip.MetadataJson);
    private static MovieAssemblyDto ToDto(MovieAssembly assembly) => new(assembly.Id, assembly.GenerationJobId, assembly.AssetId, assembly.Status, assembly.OutputFormat, assembly.MetadataJson, assembly.CreatedAt, assembly.CompletedAt);
}

public sealed class MovieStudioValidationException(string message) : Exception(message);
