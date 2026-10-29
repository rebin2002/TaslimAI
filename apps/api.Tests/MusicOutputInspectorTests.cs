using System.Buffers.Binary;
using Taslim.Api.Contracts;
using Taslim.Api.Music;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MusicOutputInspectorTests
{
    [Fact]
    public void Rejects_malformed_m4a_that_only_contains_an_ftyp_marker()
    {
        var content = new byte[12];
        "ftyp"u8.CopyTo(content.AsSpan(4));

        var result = CreateResult(content);

        Assert.Throws<MusicOutputInvalidException>(() => MusicOutputInspector.Validate(result, 1024));
    }

    [Fact]
    public void Accepts_m4a_with_a_bounded_ftyp_box_and_audio_brand()
    {
        var content = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), 24);
        "ftyp"u8.CopyTo(content.AsSpan(4, 4));
        "M4A "u8.CopyTo(content.AsSpan(8, 4));

        var result = CreateResult(content);

        var output = MusicOutputInspector.Validate(result, 1024);

        Assert.Equal("audio/mp4", output.ContentType);
        Assert.Equal("m4a", output.Format);
    }

    private static MusicProviderResult CreateResult(byte[] content) =>
        new(content, "audio/mp4", "m4a", 1, new MusicProviderUsage(null, null, null, null, 0));
}
