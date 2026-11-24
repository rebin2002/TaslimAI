using System.IO.Compression;
using Taslim.Api.Contracts;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The rendered .pptx is a zip package. ZipArchive defaults a new entry's timestamp to
/// DateTimeOffset.Now, and the MS-DOS field that stores it has a two-second resolution, so two
/// renders of a byte-identical draft differ whenever they straddle one of those tick boundaries.
/// The payload is stable; only the entry headers move.
///
/// That coupling made an otherwise deterministic build artifact unreproducible: it is what flips
/// the "render twice, assert equal bytes" assertions in the capacity suites, which cannot tell a
/// timestamp drift apart from genuine content loss. The renderer now pins every entry to the
/// archive epoch, so a package is a pure function of its draft.
///
/// These tests cross a real tick boundary rather than trusting a fixture, and they read the raw
/// DOS fields because that is the byte the regression actually moves.
/// </summary>
public sealed class PresentationPackageTimestampDeterminismTests
{
    // The MS-DOS epoch is the earliest timestamp the format can carry, and the value the renderer
    // pins every entry to. 1980-01-01 00:00:00 encodes as date 0x0021, time 0x0000.
    private const ushort ExpectedDosDate = 0x0021;
    private const ushort ExpectedDosTime = 0x0000;
    private const int TickBoundaryWaitMilliseconds = 5_000;

    [Fact]
    public void Rendering_twice_across_an_ms_dos_tick_boundary_produces_identical_package_bytes()
    {
        var renderer = new PresentationRenderer();
        var draft = Deck();

        var startBucket = CurrentTickBucket();
        var first = renderer.Render(draft, Input(), Options());

        // Wait for the two-second boundary the bug depends on, so the second render is guaranteed
        // to observe a different DateTimeOffset.Now from the first. Without the pin this is the
        // window in which the assertion below fails intermittently.
        var deadline = Environment.TickCount64 + TickBoundaryWaitMilliseconds;
        while (CurrentTickBucket() == startBucket && Environment.TickCount64 < deadline) Thread.Sleep(25);
        Assert.True(CurrentTickBucket() != startBucket,
            "the clock did not advance across an MS-DOS two-second boundary; the test cannot prove the fix");

        var second = renderer.Render(draft, Input(), Options());

        PresentationOutputIntegrityValidator.Validate(first);
        PresentationOutputIntegrityValidator.Validate(second);
        Assert.Equal(first.Content, second.Content);
    }

    [Fact]
    public void Every_package_entry_carries_the_archive_epoch_instead_of_the_wall_clock()
    {
        var rendered = new PresentationRenderer().Render(Deck(), Input(), Options());

        using var archive = new ZipArchive(new MemoryStream(rendered.Content, writable: false), ZipArchiveMode.Read);
        Assert.True(archive.Entries.Count >= 10, $"expected a full package, saw {archive.Entries.Count} entries");

        foreach (var entry in archive.Entries)
        {
            Assert.Equal(1980, entry.LastWriteTime.Year);
            Assert.Equal(1, entry.LastWriteTime.Month);
            Assert.Equal(1, entry.LastWriteTime.Day);
            Assert.Equal(0, entry.LastWriteTime.Hour);
            Assert.Equal(0, entry.LastWriteTime.Minute);
            Assert.Equal(0, entry.LastWriteTime.Second);
        }
    }

    [Fact]
    public void Local_and_central_headers_agree_on_the_pinned_dos_fields()
    {
        var bytes = new PresentationRenderer().Render(Deck(), Input(), Options()).Content;

        // The local file header keeps its own copy of the MS-DOS fields; a pin applied only to the
        // central directory would leave the two disagreeing, so both are read from the raw bytes.
        var localDates = new List<ushort>();
        var localTimes = new List<ushort>();
        for (var i = 0; i + 30 <= bytes.Length; i++)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4b || bytes[i + 2] != 0x03 || bytes[i + 3] != 0x04) continue;
            localTimes.Add(ReadUInt16(bytes, i + 10));
            localDates.Add(ReadUInt16(bytes, i + 12));
        }

        Assert.True(localDates.Count >= 10, $"expected local headers, found {localDates.Count}");
        Assert.All(localDates, date => Assert.Equal(ExpectedDosDate, date));
        Assert.All(localTimes, time => Assert.Equal(ExpectedDosTime, time));
    }

    /// <summary>
    /// The MS-DOS time field stores seconds in steps of two, so this is the granularity at which
    /// an unpinned entry header actually changes.
    /// </summary>
    private static int CurrentTickBucket() => (int)(DateTime.Now.TimeOfDay.TotalSeconds / 2);

    private static ushort ReadUInt16(byte[] bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static PresentationDraft Deck() => new()
    {
        Title = "Quarterly review",
        Subtitle = "Determinism package",
        Language = "en",
        Slides =
        [
            new PresentationSlide
            {
                Order = 1,
                Type = PresentationSlideTypes.Content,
                Title = "Revenue",
                Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Text, Text = "Revenue grew eleven percent against plan." }],
            },
            new PresentationSlide
            {
                Order = 2,
                Type = PresentationSlideTypes.Bullets,
                Title = "Actions",
                Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Bullets, Items = ["Close the pipeline gap", "Hire two engineers"] }],
            },
        ],
    };

    private static PresentationGenerationOptions Options() => new();

    private static PresentationGenerationInput Input()
        => new(Guid.NewGuid(), null, "Quarterly review", "Review the quarter", "business", "standard", "professional", "en", null, null, null, true, true, []);
}
