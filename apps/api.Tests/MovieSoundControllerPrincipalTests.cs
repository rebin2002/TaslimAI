using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Controllers;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieSoundControllerPrincipalTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Malformed_authenticated_principal_returns_unauthorized_for_all_sound_actions(string? identifier)
    {
        var service = new RecordingMovieSoundService();
        var controller = CreateController(service, identifier);
        var projectId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var requests = new Func<Task<IActionResult>>[]
        {
            () => controller.GetLibrary(projectId, CancellationToken.None),
            () => controller.AddLibraryReference(projectId, new MovieSoundLibraryReferenceRequest { AssetId = Guid.NewGuid() }, CancellationToken.None),
            () => controller.GetSceneTracks(sceneId, CancellationToken.None),
            () => controller.CreateSceneTrack(sceneId, new MovieSoundTrackRequest(), CancellationToken.None),
            () => controller.GetShotTracks(shotId, CancellationToken.None),
            () => controller.CreateShotTrack(shotId, new MovieSoundTrackRequest(), CancellationToken.None),
            () => controller.GetTrack(trackId, CancellationToken.None),
            () => controller.Review(trackId, new MovieSoundReviewRequest(), CancellationToken.None),
        };

        foreach (var request in requests)
            Assert.IsType<UnauthorizedResult>(await request());

        Assert.Equal(0, service.Calls);
    }

    private static MovieSoundController CreateController(RecordingMovieSoundService service, string? identifier)
    {
        var claims = identifier is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, identifier) };
        var controller = new MovieSoundController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        claims,
                        authenticationType: "Test")),
                },
            },
        };
        return controller;
    }

    private sealed class RecordingMovieSoundService : IMovieSoundService
    {
        public int Calls { get; private set; }

        public Task<MovieSoundTrackDto?> CreateForSceneAsync(Guid userId, Guid sceneId, MovieSoundTrackRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<MovieSoundTrackDto?>(null);
        }

        public Task<MovieSoundTrackDto?> CreateForShotAsync(Guid userId, Guid shotId, MovieSoundTrackRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<MovieSoundTrackDto?>(null);
        }

        public Task<MovieSoundTrackListDto?> GetForSceneAsync(Guid userId, Guid sceneId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<MovieSoundTrackListDto?>(null);
        }

        public Task<MovieSoundTrackListDto?> GetForShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<MovieSoundTrackListDto?>(null);
        }

        public Task<MovieSoundTrackDto?> GetAsync(Guid userId, Guid trackId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<MovieSoundTrackDto?>(null);
        }

        public Task<MovieSoundLibraryDto?> GetLibraryAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<MovieSoundLibraryDto?>(null);
        }

        public Task<MovieSoundLibraryReferenceDto?> AddLibraryReferenceAsync(Guid userId, Guid movieProjectId, MovieSoundLibraryReferenceRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<MovieSoundLibraryReferenceDto?>(null);
        }

        public Task<MovieSoundTrackDto?> ReviewAsync(Guid userId, Guid trackId, MovieSoundReviewRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<MovieSoundTrackDto?>(null);
        }
    }
}
