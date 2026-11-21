using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Issue #708 (Music Studio), negative-acceptance contract disclosed by the audit behind PR #758.
//
// Bug surface: MusicGenerationRequestValidator currently admits a prompt when
// `!string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 3`. .Trim() only removes
// characters that Unicode whitespace classification maps to white_space, so strings built
// from Unicode bidi format controls and zero-width characters (U+200F RLM, U+061C ALM,
// U+202A LRE, U+200B ZWSP, ...) are NOT whitespace and NOT trimmed away. Visually empty
// Sorani/Arabic prompts therefore pass validation and reach the billable admission path:
// IUsageCostControl.CheckPreflightAsync (cost reservation) followed by GenerationJob and
// UsageTransaction persistence.
//
// This file pins the contract, not an implementation: an invisible-only `description` or
// `purpose` must be rejected with MUSIC_REQUEST_INVALID *before* cost reservation, job or
// usage persistence, and before any provider call, while genuinely visible Sorani text
// keeps flowing through the current permitted admission semantics.
//
// STATUS AT THE BASE COMMIT (current main): RED for the two negative tests, which fail with
// "Expected: BadRequest / Actual: Accepted", confirming that an invisible-only prompt is
// admitted to the billable path. The two visible-text controls are GREEN and must stay
// GREEN once the guard is fixed. This is a test-only contract PR: no runtime, validator,
// contract or UI source is changed here to force green.
//
// Deliberately provider-free and deterministic: only the existing fake music provider
// (DeterministicMusicProvider, registered by MusicGenerationApiFactory) and the in-memory
// test database are used. No Suno/Mubert/Stable Audio calls, no charges, no config changes.
//
// Unicode policy: to avoid normalization ambiguity, every control character below is
// written as an explicit UTF-16 escape (single BMP code point, no composition possible),
// and the classification helpers at the bottom define this test's own safe expectation:
// format-control/zero-width code points must never count toward the minimum visible
// prompt length. These Cc/Cf code points are not composed or decomposed by NFC/NFD.
public sealed class MusicInvisibleLyricsCostAdmissionTests : IClassFixture<MusicGenerationApiFactory>
{
    private readonly MusicGenerationApiFactory factory;

    public MusicInvisibleLyricsCostAdmissionTests(MusicGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    // Explicit, normalization-stable code points used as the invisible controls.
    private const char RightToLeftMark = '\u200F';       // RLM (Cf, bidi format control)
    private const char ArabicLetterMark = '\u061C';      // ALM (Cf, bidi format control)
    private const char LeftToRightEmbedding = '\u202A';  // LRE (Cf, bidi embedding)
    private const char RightToLeftEmbedding = '\u202B';  // RLE (Cf, bidi embedding)
    private const char PopDirectionalFormat = '\u202C';  // PDF (Cf, direction terminator)
    private const char ZeroWidthSpace = '\u200B';        // ZWSP (invisible space)
    private const char ZeroWidthNonJoiner = '\u200C';    // ZWNJ (Cf, invisible joiner control)

    [Fact]
    public async Task Bidi_format_control_only_description_is_rejected_before_cost_reservation()
    {
        // "RLM + ALM + LRE + RLE + PDF" - visually empty, but String.Trim() keeps all of it,
        // so today `Trim().Length >= 3` is satisfied and the request reaches the billable path.
        var invisibleOnly = new string([RightToLeftMark, ArabicLetterMark, LeftToRightEmbedding, RightToLeftEmbedding, PopDirectionalFormat]);
        Assert.True(IsOnlyInvisibleFormatControls(invisibleOnly));
        Assert.True(invisibleOnly.Trim().Length >= 3); // documents why the current guard is insufficient

        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-invisible-desc-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Invisible description probe",
            description = invisibleOnly,
            purpose = "A genuinely visible purpose describing a launch video bed.",
            genre = "ambient",
            mood = "calm",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "ku",
        });

        await AssertRejectedBeforeAdmission(response, auth);
    }

    [Fact]
    public async Task Zero_width_only_purpose_is_rejected_before_cost_reservation()
    {
        // ZWSP + ZWNJ + RLM repeated - zero visible glyphs, but survives Trim().
        var invisibleOnly = new string([ZeroWidthSpace, ZeroWidthNonJoiner, RightToLeftMark, ZeroWidthSpace, RightToLeftMark, ZeroWidthNonJoiner]);
        Assert.True(IsOnlyInvisibleFormatControls(invisibleOnly));
        Assert.True(invisibleOnly.Trim().Length >= 3);

        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-invisible-purpose-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Invisible purpose probe",
            description = "A warm Sorani folk arrangement with visible lyric intent.",
            purpose = invisibleOnly,
            genre = "folk",
            mood = "inspiring",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "ku",
        });

        await AssertRejectedBeforeAdmission(response, auth);
    }

    [Fact]
    public async Task Visible_sorani_lyrics_text_still_passes_admission()
    {
        // Positive control (GREEN at base): ordinary visible Sorani (Arabic-script) lyrics
        // text must continue through the current permitted admission semantics unchanged.
        // "ئەم گۆرانییە بۆ منداڵانی کوردستان" - this song is for the children of Kurdistan.
        const string visibleSorani = "ئەم گۆرانییە بۆ منداڵانی کوردستان";
        Assert.True(VisibleGlyphCount(visibleSorani) >= 3);

        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-visible-ku-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Sorani visible control",
            description = visibleSorani,
            purpose = "مۆسیقای بەکگرۆوند بۆ ڤیدیۆیەک",
            genre = "world",
            mood = "inspiring",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "ku",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = JsonSerializer.Deserialize<CreateMusicGenerationResponse>(
            await response.Content.ReadAsStringAsync(), JsonOptions)!;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.True(await db.GenerationJobs.AsNoTracking().AnyAsync(job => job.Id == envelope.Job.Id));

        // Drain the fake-provider job so the shared fixture database settles.
        await WaitForTerminal(client, envelope.Job.Id);
    }

    [Fact]
    public async Task Invisible_controls_wrapping_visible_sorani_do_not_break_admission()
    {
        // Classification boundary control (GREEN at base, must stay GREEN): bidi controls
        // embedded around real visible text must not flip an otherwise-visible prompt into
        // a rejection. Visible content, not invisibility, decides admission.
        var mixed = $"{RightToLeftMark}{ZeroWidthSpace}ئەم گۆرانییە کوردییە{PopDirectionalFormat}{ZeroWidthNonJoiner}";
        Assert.False(IsOnlyInvisibleFormatControls(mixed));
        Assert.True(VisibleGlyphCount(mixed) >= 3);

        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-mixed-ku-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Mixed control probe",
            description = mixed,
            purpose = "Background bed for a Sorani-language short film.",
            genre = "ambient",
            mood = "calm",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "ku",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var envelope = JsonSerializer.Deserialize<CreateMusicGenerationResponse>(
            await response.Content.ReadAsStringAsync(), JsonOptions)!;
        await WaitForTerminal(client, envelope.Job.Id);
    }

    // ---- contract assertions -----------------------------------------------------------------

    // The rejection must be a 400 MUSIC_REQUEST_INVALID, and nothing billable may have been
    // touched: no cost-reservation usage row, no GenerationJob, no Asset for this user.
    // (DeterministicMusicProvider is only ever reached from a persisted job's handler, so the
    // absence of any GenerationJob row also proves no provider call happened.)
    private async Task AssertRejectedBeforeAdmission(HttpResponseMessage response, AuthResponse auth)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid,
            body.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(job => job.CreatedByUserId == auth.User.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(usage => usage.UserId == auth.User.Id));
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(asset => asset.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    // ---- explicit, normalization-stable classification (this contract's own definition) -----

    // Code points that carry no visible glyph: Unicode bidi format controls and the
    // zero-width joiner/space family. Deliberately enumerated here instead of trusting
    // String.IsWhiteSpace/Trim, which is exactly the current gap under test. NFC/NFD do not
    // compose or decompose these Cc/Cf code points, so classification is deterministic.
    private static readonly HashSet<char> InvisibleFormatControls =
    [
        '\u061C',  // ALM  Arabic Letter Mark
        '\u200B',  // ZWSP Zero Width Space
        '\u200C',  // ZWNJ Zero Width Non-Joiner
        '\u200D',  // ZWJ  Zero Width Joiner
        '\u200E',  // LRM
        '\u200F',  // RLM
        '\u202A',  // LRE
        '\u202B',  // RLE
        '\u202C',  // PDF
        '\u202D',  // LRO
        '\u202E',  // RLO
        '\u2066',  // LRI
        '\u2067',  // RLI
        '\u2068',  // FSI
        '\u2069',  // PDI
    ];

    private static bool IsOnlyInvisibleFormatControls(string value) =>
        value.Length > 0 && value.All(IsInvisibleFormatControl);

    private static int VisibleGlyphCount(string value) =>
        value.Count(character => !IsInvisibleFormatControl(character) && !char.IsWhiteSpace(character));

    private static bool IsInvisibleFormatControl(char character) =>
        InvisibleFormatControls.Contains(character)
        || UnicodeCategory.Format == GetCategory(character);

    // Kept separate so a future .NET change cannot silently widen the classification under test.
    private static UnicodeCategory GetCategory(char character) => CharUnicodeInfo.GetUnicodeCategory(character);

    // ---- existing test infrastructure helpers (local copies; no shared file edits) ----------

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register",
            new { displayName = "Invisible Music Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Music job {id} did not reach a terminal state.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
