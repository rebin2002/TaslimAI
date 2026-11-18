using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Deterministic, provider-free regression lock for the Music Studio failure-log
/// boundary named in docs/MUSIC_STUDIO_ARCHITECTURE.md ("Upstream response bodies
/// are never copied into job messages, usage metadata, or logs") and
/// docs/OPERATIONS_RUNBOOK.md ("Logs deliberately omit ... full prompts").
///
/// Every scenario runs against one locally injected deterministic provider inside a
/// single shared host, so no music provider, credential, cost gate or charging path
/// is contacted and no extra hosted worker loop is added to the suite. Content
/// markers stand in for lyric lines and private text; their absence from every
/// captured log record and persisted failure field is the assertion.
/// </summary>
public sealed class MusicStudioFailureLogPrivacyFactory : GenerationJobsApiFactory
{
    public MusicStudioFailureLogCapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "test");
        builder.UseSetting("MusicGeneration:Model", "music-test");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(Logs);
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<IMusicGenerationProvider>(new MusicStudioFailureLogProbeProvider());
        });
    }
}

/// <summary>
/// Local stand-in for a misbehaving upstream adapter. It only fails when the caller's
/// own description carries the marker, which is the worst realistic case: an adapter
/// quoting the customer's words into its own exception text. The failure log must
/// still describe only the failure.
/// </summary>
internal sealed class MusicStudioFailureLogProbeProvider : IMusicGenerationProvider
{
    public string Key => "test";

    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
    {
        if (request.Description.Contains(MusicStudioFailureLogPrivacyTests.QuotedDescriptionMarker, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"provider refused {request.Description} :: {string.Join(" / ", (request.Lyrics ?? []).SelectMany(section => section?.Lines ?? []))}");

        return Task.FromResult(new MusicProviderResult(
            new ReadOnlyMemory<byte>(new byte[512]),
            "audio/mpeg",
            "mp3",
            request.DurationSeconds,
            new MusicProviderUsage(1, 1, 0.004m, 0.004m, 2, CostBasis: UsageCostBasis.Actual)));
    }
}

public sealed record MusicStudioLogRecord(string Category, LogLevel Level, string Message, string? ExceptionText, string? StateValues);

public sealed class MusicStudioFailureLogCapturingLoggerProvider : ILoggerProvider
{
    private readonly List<MusicStudioLogRecord> records = [];
    public IReadOnlyList<MusicStudioLogRecord> Records { get { lock (records) return records.ToArray(); } }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
    public void Dispose() { }

    private sealed class CapturingLogger(MusicStudioFailureLogCapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string? values = null;
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                values = string.Join("|", pairs.Where(pair => pair.Key != "{OriginalFormat}").Select(pair => $"{pair.Key}={pair.Value}"));
            lock (owner.records)
                owner.records.Add(new MusicStudioLogRecord(category, logLevel, formatter(state, exception), exception?.ToString(), values));
        }
    }
}

public sealed class MusicStudioFailureLogPrivacyTests : IClassFixture<MusicStudioFailureLogPrivacyFactory>
{
    // Reserved markers. They are not lyric or file content; they are the tokens whose
    // absence proves neither lyric text nor private reference material reached a
    // failure record.
    public const string QuotedDescriptionMarker = "TESTONLYDESCRIPTIONMARKER9ZR";
    private const string LyricsMarker = "TESTONLYLYRICMARKER7QM";
    private const string InstructionsMarker = "TESTONLYINSTRUCTIONSMARKER4KD";
    private const string TitleMarker = "TESTONLYTITLEMARKER2VT";
    // Synthetic Arabic and Sorani lyric lines. They stand in for the realistic
    // private content of this product's RTL drafts (#708): if the failure record
    // echoed user text, these are the words it would echo. They quote no user
    // data and are the only Arabic-script content in this test class, so their
    // absence from every failure field and captured log record is the lock.
    private const string ArabicLyricsMarker = "[TEST ONLY] لَا تُعِدْ كِتَابَةَ هَذَا السَّطْر";
    private const string SoraniLyricsMarker = "[TEST ONLY] ئەم دێڕە تەنها بۆ تاقیکردنەوەیە";
    private static readonly Guid ReferenceAssetId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly MusicStudioFailureLogPrivacyFactory factory;

    public MusicStudioFailureLogPrivacyTests(MusicStudioFailureLogPrivacyFactory factory) => this.factory = factory;

    private static string SongInputJson =>
        "{\"Description\":\"a calm song about a long road\",\"Purpose\":\"Lock the failure log boundary\",\"Genre\":\"ambient\",\"Mood\":\"calm\",\"DurationSeconds\":30," +
        "\"VocalPreference\":\"vocal\",\"Language\":\"auto\",\"Title\":\"" + TitleMarker + "\"," +
        "\"AdditionalInstructions\":\"" + InstructionsMarker + "\"," +
        "\"CreationMode\":\"song_with_vocals\"," +
        "\"Lyrics\":[{\"Section\":\"verse\",\"Lines\":[\"" + LyricsMarker + " one\",\"" + LyricsMarker + " two\"]}]," +
        "\"LyricsLanguage\":\"ku\",\"LyricsDialect\":\"sorani\",\"LyricsRightsConfirmed\":true," +
        "\"ReferenceAssets\":[{\"MediaType\":\"audio\",\"AssetId\":\"" + ReferenceAssetId + "\"}],\"ReferenceRightsConfirmed\":true}";

    // The same persisted-input stress case with Arabic and Sorani verse/chorus
    // lines, admitted language ar with its explicit iraqi_baghdadi dialect.
    private static string RtlLyricsSongInputJson =>
        "{\"Description\":\"a calm song about a long road\",\"Purpose\":\"Lock the failure log boundary\",\"Genre\":\"ambient\",\"Mood\":\"calm\",\"DurationSeconds\":30," +
        "\"VocalPreference\":\"vocal\",\"Language\":\"auto\",\"Title\":\"" + TitleMarker + "\"," +
        "\"AdditionalInstructions\":\"" + InstructionsMarker + "\"," +
        "\"CreationMode\":\"song_with_vocals\"," +
        "\"Lyrics\":[{\"Section\":\"verse\",\"Lines\":[\"" + ArabicLyricsMarker + "\"]},{\"Section\":\"chorus\",\"Lines\":[\"" + SoraniLyricsMarker + "\"]}]," +
        "\"LyricsLanguage\":\"ar\",\"LyricsDialect\":\"iraqi_baghdadi\",\"LyricsRightsConfirmed\":true," +
        "\"ReferenceAssets\":[{\"MediaType\":\"audio\",\"AssetId\":\"" + ReferenceAssetId + "\"}],\"ReferenceRightsConfirmed\":true}";

    [Fact(DisplayName = "Music Studio: sung-lyrics admission fails closed without a provider and logs no lyric text")]
    public async Task SungLyricsAdmissionLogsNoLyricText()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var before = factory.Logs.Records.Count;

        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "a calm song about a long road",
            purpose = "Lock the failure log boundary",
            genre = "ambient",
            mood = "calm",
            durationSeconds = 30,
            creationMode = "song_with_vocals",
            vocalPreference = "vocal",
            language = "auto",
            lyricsLanguage = "ku",
            lyricsDialect = "sorani",
            lyricsRightsConfirmed = true,
            lyrics = new[] { new { section = "verse", lines = new[] { $"{LyricsMarker} one", $"{LyricsMarker} two" } } },
            referenceAssets = new[] { new { mediaType = "audio", assetId = ReferenceAssetId } },
            referenceRightsConfirmed = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(LyricsMarker, envelope, StringComparison.OrdinalIgnoreCase);

        // Fail-closed admission happens before job creation, so nothing is persisted:
        // no cost preflight, no usage row, no provider call.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(job => job.CreatedByUserId == auth.User.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(usage => usage.UserId == auth.User.Id));

        AssertNoMarkerInLogs(before, "sung-lyrics admission");
    }

    [Fact(DisplayName = "Music Studio: a wrong-typed lyrics field is refused without echoing the rejected value into logs")]
    public async Task MalformedLyricsShapeLogsNoLyricText()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var before = factory.Logs.Records.Count;

        var payload = "{\"workspaceId\":\"" + auth.PersonalWorkspace.Id + "\"," +
            "\"description\":\"a calm song about a long road\",\"purpose\":\"Lock the failure log boundary\"," +
            "\"genre\":\"ambient\",\"mood\":\"calm\",\"durationSeconds\":30," +
            "\"creationMode\":\"song_with_vocals\",\"vocalPreference\":\"vocal\",\"language\":\"auto\"," +
            "\"lyrics\":\"" + LyricsMarker + "\"}";
        var response = await SendRawJsonAsync(client, HttpMethod.Post, "/api/music-generation/jobs", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoMarkerInLogs(before, "malformed lyrics shape");
    }

    [Fact(DisplayName = "Music Studio: persisted lyric input never surfaces in the job failure record or its logs")]
    public async Task PersistedLyricInputStaysOutOfFailureRecords()
    {
        // The generic job endpoint is the provider-free route that does persist an
        // arbitrary music input, so it is the honest stress case: the lyric text
        // genuinely exists in InputJson, and the job then fails.
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var before = factory.Logs.Records.Count;

        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = "music.generate",
            title = TitleMarker + " persisted lyric input",
            inputJson = SongInputJson,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<GenerationJobDto>(WebJson))!;

        var terminal = await WaitForTerminalAsync(client, created.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.False(string.IsNullOrWhiteSpace(terminal.ErrorCode));
        Assert.DoesNotContain(LyricsMarker, terminal.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(InstructionsMarker, terminal.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == terminal.Id);
        // The lyric text is persisted exactly once, as the job input, and nowhere in
        // the failure record derived from it.
        Assert.Contains(LyricsMarker, job.InputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(LyricsMarker, job.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        foreach (var usage in await db.UsageTransactions.AsNoTracking()
                     .Where(item => item.RequestId == $"generation:{terminal.Id:N}").ToListAsync())
        {
            Assert.DoesNotContain(LyricsMarker, usage.SafeMetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LyricsMarker, usage.FailureCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var attempt in await db.GenerationProviderAttempts.AsNoTracking()
                     .Where(item => item.GenerationJobId == terminal.Id).ToListAsync())
        {
            Assert.DoesNotContain(LyricsMarker, attempt.SafeMetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LyricsMarker, attempt.FailureCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        AssertNoMarkerInLogs(before, "persisted lyric input failure");
    }

    [Fact(DisplayName = "Music Studio: a provider failure quoting the request still logs only the exception type")]
    public async Task ProviderFailureQuotingRequestIsNotLogged()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var before = factory.Logs.Records.Count;

        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = QuotedDescriptionMarker + " a calm instrumental",
            purpose = "Lock the failure log boundary",
            genre = "ambient",
            mood = "calm",
            durationSeconds = 30,
            creationMode = "instrumental",
            vocalPreference = "instrumental",
            language = "auto",
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>(WebJson))!.Job;
        var terminal = await WaitForTerminalAsync(client, created.Id);

        Assert.Equal("Failed", terminal.Status);
        Assert.DoesNotContain(QuotedDescriptionMarker, terminal.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var observed = factory.Logs.Records.Skip(before).ToArray();
        // Anti-silent-pass guard: the failure really was logged by the application.
        Assert.Contains(observed, record =>
            record.Level == LogLevel.Error && record.Message.Contains("Generation job execution failed"));
        Assert.False(
            observed.Any(record => CarriesMarker(record.Message) || CarriesMarker(record.ExceptionText) || CarriesMarker(record.StateValues)),
            "a Music Studio failure record must not quote the caller's own words");
    }

    [Fact(DisplayName = "Music Studio: Arabic/Sorani sung-lyrics admission fails closed and echoes no script text")]
    public async Task ArabicAndSoraniSungLyricsAdmissionEchoesNoScriptText()
    {
        // Mirror of the ASCII admission lock for the product's two RTL languages:
        // a fully-formed Arabic song request (valid iraqi_baghdadi dialect) and
        // Sorani lyric lines must be refused on the capability gate alone — a
        // code and a fixed English message. No Arabic-script codepoint, and no
        // fragment of the caller's lyric lines, may appear in the response
        // envelope or in any captured log record.
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var before = factory.Logs.Records.Count;

        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "a calm song about a long road",
            purpose = "Lock the failure log boundary",
            genre = "ambient",
            mood = "calm",
            durationSeconds = 30,
            creationMode = "song_with_vocals",
            vocalPreference = "vocal",
            language = "auto",
            lyricsLanguage = "ar",
            lyricsDialect = "iraqi_baghdadi",
            lyricsRightsConfirmed = true,
            lyrics = new[]
            {
                new { section = "verse", lines = new[] { ArabicLyricsMarker } },
                new { section = "chorus", lines = new[] { SoraniLyricsMarker } },
            },
            referenceAssets = new[] { new { mediaType = "audio", assetId = ReferenceAssetId } },
            referenceRightsConfirmed = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ArabicLyricsMarker, envelope, StringComparison.Ordinal);
        Assert.DoesNotContain(SoraniLyricsMarker, envelope, StringComparison.Ordinal);
        // Stronger structural lock: the rejection envelope carries no Arabic or
        // Sorani codepoint at all, in any range the two scripts use.
        Assert.False(ContainsRtlScriptText(envelope), $"rejection envelope carried RTL script text: {envelope}");

        AssertNoMarkerInLogs(before, "Arabic/Sorani sung-lyrics admission");
    }

    [Fact(DisplayName = "Music Studio: persisted Arabic/Sorani lyric input never surfaces in the job failure record or its logs")]
    public async Task PersistedArabicAndSoraniLyricInputStaysOutOfFailureRecords()
    {
        // Same honest stress case as the ASCII lock, with the lyric text
        // genuinely persisted in InputJson in both RTL scripts: the failure
        // record derived from that job must not carry it anywhere.
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var before = factory.Logs.Records.Count;

        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = "music.generate",
            title = TitleMarker + " persisted RTL lyric input",
            inputJson = RtlLyricsSongInputJson,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<GenerationJobDto>(WebJson))!;

        var terminal = await WaitForTerminalAsync(client, created.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.False(string.IsNullOrWhiteSpace(terminal.ErrorCode));
        Assert.DoesNotContain(ArabicLyricsMarker, terminal.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(SoraniLyricsMarker, terminal.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.False(ContainsRtlScriptText(terminal.ErrorMessage ?? string.Empty));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == terminal.Id);
        // Persisted exactly once, as the job input; absent from the failure field.
        Assert.Contains(ArabicLyricsMarker, job.InputJson, StringComparison.Ordinal);
        Assert.Contains(SoraniLyricsMarker, job.InputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(ArabicLyricsMarker, job.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(SoraniLyricsMarker, job.ErrorMessage ?? string.Empty, StringComparison.Ordinal);

        foreach (var usage in await db.UsageTransactions.AsNoTracking()
                     .Where(item => item.RequestId == $"generation:{terminal.Id:N}").ToListAsync())
        {
            Assert.DoesNotContain(ArabicLyricsMarker, usage.SafeMetadataJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(SoraniLyricsMarker, usage.SafeMetadataJson ?? string.Empty, StringComparison.Ordinal);
        }
        foreach (var attempt in await db.GenerationProviderAttempts.AsNoTracking()
                     .Where(item => item.GenerationJobId == terminal.Id).ToListAsync())
        {
            Assert.DoesNotContain(ArabicLyricsMarker, attempt.SafeMetadataJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(SoraniLyricsMarker, attempt.SafeMetadataJson ?? string.Empty, StringComparison.Ordinal);
        }

        AssertNoMarkerInLogs(before, "persisted Arabic/Sorani lyric input failure");
    }

    /// <summary>True when the text contains any codepoint from the Arabic script
    /// ranges Sorani and Arabic are written in (base block, supplement, and the
    /// two presentation-form blocks).</summary>
    private static bool ContainsRtlScriptText(string value) => value.Any(ch =>
        ch is >= '\u0600' and <= '\u06FF'
        or >= '\u0750' and <= '\u077F'
        or >= '\uFB50' and <= '\uFDFF'
        or >= '\uFE70' and <= '\uFEFF');

    private static bool CarriesMarker(string? text) =>
        text is not null
        && (text.Contains(LyricsMarker, StringComparison.OrdinalIgnoreCase)
            || text.Contains(QuotedDescriptionMarker, StringComparison.OrdinalIgnoreCase)
            || text.Contains(InstructionsMarker, StringComparison.OrdinalIgnoreCase)
            || text.Contains(TitleMarker, StringComparison.OrdinalIgnoreCase)
            || text.Contains(ReferenceAssetId.ToString(), StringComparison.OrdinalIgnoreCase)
            || text.Contains(ArabicLyricsMarker, StringComparison.Ordinal)
            || text.Contains(SoraniLyricsMarker, StringComparison.Ordinal));

    private void AssertNoMarkerInLogs(int from, string scenario)
    {
        var observed = factory.Logs.Records.Skip(from).ToArray();
        // Guard against a silent pass: the scenario must have produced logs, including
        // the application's own categories.
        Assert.NotEmpty(observed);
        Assert.Contains(observed, record => record.Category.StartsWith("Taslim.Api", StringComparison.Ordinal));
        foreach (var record in observed.Where(record => CarriesMarker(record.Message) || CarriesMarker(record.ExceptionText) || CarriesMarker(record.StateValues)))
            Assert.Fail($"{scenario}: marker reached the failure log [{record.Category}/{record.Level}] {record.Message}");
    }

    private static async Task<GenerationJobDto> WaitForTerminalAsync(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            if (job is not null && job.Status is "Failed" or "Succeeded" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Music job {id} did not reach a terminal state.");
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Failure Log Privacy Tester",
            email = $"music-log-privacy-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendRawJsonAsync(HttpClient client, HttpMethod method, string path, string rawJson)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
}
