using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Caption export has to be round-trip safe: a track downloaded from
/// <c>GET /api/movie-studio/caption-tracks/{trackId}/export</c> must be
/// re-importable through <c>POST /api/movie-studio/projects/{id}/caption-tracks/import</c>
/// without losing or rejecting content.
///
/// Both supported containers end a cue at the first blank line, so a cue whose
/// text carries an interior blank line cannot be written verbatim. The exporters
/// used to do exactly that, so an owner could download a track that the same
/// product refused to import (SRT) or silently truncated (WebVTT).
/// </summary>
public sealed class MovieCaptionExportRoundTripTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieCaptionExportRoundTripTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public async Task Exported_document_keeps_every_cue_block_inside_the_container_grammar(string format)
    {
        var owner = await Register($"Caption Grammar {format} Owner");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, $"Grammar {format}");
        await AddCaptionCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:03.000", "The first line.\n\nThe carried line.");
        await AddCaptionCue(owner.Client, track.Id, 2, "00:00:04.000", "00:00:06.000", "A clean follow-up cue.");

        var exported = await Export(owner.Client, track.Id, format);

        // Every blank-line separated block must carry a timecode line; a stray
        // block is what makes the document unimportable.
        var blocks = CueBlocks(exported, format);
        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, block => Assert.Contains("-->", block, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public async Task Exported_track_reimports_without_losing_cues_or_text(string format)
    {
        var owner = await Register($"Caption RoundTrip {format} Owner");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, $"Round trip {format}");
        await AddCaptionCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:03.000", "Line one\nLine two\n\nLine three");
        await AddCaptionCue(owner.Client, track.Id, 2, "00:00:05.000", "00:00:07.000", "Second cue stays intact.");

        var exported = await Export(owner.Client, track.Id, format);

        using var reimport = await SendWithCsrf(owner.Client, HttpMethod.Post,
            $"/api/movie-studio/projects/{owner.MovieId}/caption-tracks/import",
            new
            {
                name = $"Reimported {format}",
                trackType = MovieCaptionTrackTypes.Subtitle,
                language = "en",
                status = MovieCaptionTrackStatuses.Draft,
                format,
                content = exported,
            });

        Assert.True(reimport.StatusCode == HttpStatusCode.OK, await reimport.Content.ReadAsStringAsync());
        var reimported = (await reimport.Content.ReadFromJsonAsync<MovieCaptionTrackDto>())!;

        // Two authored cues in, two out: nothing may be dropped, split into a
        // phantom block, or collapsed into the neighbouring cue.
        Assert.Equal(2, reimported.Cues.Count);
        Assert.Equal("00:00:01.000", reimported.Cues[0].StartTimecode);
        Assert.Equal("00:00:05.000", reimported.Cues[1].StartTimecode);
        var combined = string.Join("\n", reimported.Cues.Select(item => item.Text));
        Assert.Contains("Line one", combined, StringComparison.Ordinal);
        Assert.Contains("Line two", combined, StringComparison.Ordinal);
        Assert.Contains("Line three", combined, StringComparison.Ordinal);
        Assert.Contains("Second cue stays intact.", combined, StringComparison.Ordinal);
    }

    /// <summary>
    /// An exported file is caption content, so the read boundary that guards the
    /// track JSON has to guard the download too.
    /// </summary>
    [Fact]
    public async Task Export_denies_a_non_member_and_still_serves_the_owner()
    {
        var owner = await Register("Caption Export Owner");
        var intruder = await Register("Caption Export Intruder");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, "Private subtitles");
        await AddCaptionCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:02.000", "Private caption text.");

        using var denied = await intruder.Client.GetAsync($"/api/movie-studio/caption-tracks/{track.Id}/export?format=srt");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var deniedBody = await denied.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Private caption text.", deniedBody, StringComparison.Ordinal);

        var allowed = await Export(owner.Client, track.Id, "srt");
        Assert.Contains("Private caption text.", allowed, StringComparison.Ordinal);
    }

    private static string[] CueBlocks(string document, string format)
    {
        var normalized = document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // WebVTT opens with a header block that carries no cue text.
        return format == "vtt"
            ? blocks.Where(block => !block.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)).ToArray()
            : blocks;
    }

    /// <summary>
    /// A line that holds only spaces still terminates a cue: the WebVTT reader
    /// stops cue-text collection on any whitespace-only line, so the exporter
    /// has to fold those as well, not only truly empty ones.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public async Task Whitespace_only_interior_line_does_not_terminate_a_cue(string format)
    {
        var owner = await Register($"Caption Whitespace {format} Owner");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, $"Whitespace {format}");
        await AddCaptionCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:03.000", "Line one\n   \nLine three");

        var exported = await Export(owner.Client, track.Id, format);

        using var reimport = await SendWithCsrf(owner.Client, HttpMethod.Post,
            $"/api/movie-studio/projects/{owner.MovieId}/caption-tracks/import",
            new
            {
                name = $"Reimported whitespace {format}",
                trackType = MovieCaptionTrackTypes.Subtitle,
                language = "en",
                status = MovieCaptionTrackStatuses.Draft,
                format,
                content = exported,
            });
        Assert.True(reimport.StatusCode == HttpStatusCode.OK, await reimport.Content.ReadAsStringAsync());
        var reimported = (await reimport.Content.ReadFromJsonAsync<MovieCaptionTrackDto>())!;

        Assert.Single(reimported.Cues);
        Assert.Equal("Line one\nLine three", reimported.Cues[0].Text);
    }

    private static async Task<string> Export(HttpClient client, Guid trackId, string format)
    {
        using var export = await client.GetAsync($"/api/movie-studio/caption-tracks/{trackId}/export?format={format}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        return await export.Content.ReadAsStringAsync();
    }

    private async Task<(HttpClient Client, Guid MovieId)> Register(string displayName)
    {
        var client = factory.CreateClient();
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"caption-export-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        var movie = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = $"Caption export {Guid.NewGuid():N}",
            description = "A movie used to pin the caption export round-trip contract.",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        return (client, movie.Project.Id);
    }

    private static async Task<MovieCaptionTrackDto> CreateCaptionTrack(HttpClient client, Guid movieId, string name) =>
        await SendWithCsrf<MovieCaptionTrackDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movieId}/caption-tracks", new
        {
            name,
            trackType = MovieCaptionTrackTypes.Subtitle,
            language = "en",
            status = MovieCaptionTrackStatuses.Ready,
        });

    private static async Task<MovieCaptionCueDto> AddCaptionCue(HttpClient client, Guid trackId, int sequence, string start, string end, string text) =>
        await SendWithCsrf<MovieCaptionCueDto>(client, HttpMethod.Post, $"/api/movie-studio/caption-tracks/{trackId}/cues", new
        {
            sequence,
            startTimecode = start,
            endTimecode = end,
            text,
        });

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        using var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
