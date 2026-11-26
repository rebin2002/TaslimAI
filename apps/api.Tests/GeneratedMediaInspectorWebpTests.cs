using System.Buffers.Binary;
using System.Text;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GeneratedMediaInspectorWebpTests
{
    private static readonly GeneratedMediaDescriptor WebpDescriptor = new("image.webp", ".webp", "image/webp");

    [Fact]
    public void Persists_vp8l_dimensions_in_generated_media_metadata()
    {
        var inspection = GeneratedMediaInspector.Inspect(WebpDescriptor, BuildWebp(("JUNK", [0x01]), ("VP8L", BuildVp8L(640, 480))), null);

        Assert.Equal("webp", inspection.ContainerFormat);
        Assert.Equal(640, inspection.Width);
        Assert.Equal(480, inspection.Height);
        var metadata = GeneratedMediaInspector.MergeMetadata(null, inspection);
        Assert.Contains("\"width\":640", metadata, StringComparison.Ordinal);
        Assert.Contains("\"height\":480", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_vp8_key_frame_dimensions_for_generated_media_storage()
    {
        var inspection = GeneratedMediaInspector.Inspect(WebpDescriptor, BuildWebp(("VP8 ", BuildVp8(1536, 1024))), null);

        Assert.Equal(1536, inspection.Width);
        Assert.Equal(1024, inspection.Height);
    }

    [Fact]
    public void Keeps_malformed_webp_dimension_inspection_bounded()
    {
        var oversizedChunk = BuildWebp(("VP8L", BuildVp8L(640, 480)));
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedChunk.AsSpan(16, 4), uint.MaxValue);
        var truncatedRiff = BuildWebp(("VP8L", BuildVp8L(640, 480)));
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedRiff.AsSpan(4, 4), 4);

        foreach (var bytes in new[]
        {
            BuildWebp(("VP8L", [0x2F])),
            oversizedChunk,
            truncatedRiff,
        })
        {
            var inspection = GeneratedMediaInspector.Inspect(WebpDescriptor, bytes, null);
            Assert.Null(inspection.Width);
            Assert.Null(inspection.Height);
        }
    }

    private static byte[] BuildVp8L(int width, int height)
    {
        var payload = new byte[5];
        payload[0] = 0x2F;
        var bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), bits);
        return payload;
    }

    private static byte[] BuildVp8(int width, int height)
    {
        var payload = new byte[10];
        payload[3] = 0x9D;
        payload[4] = 0x01;
        payload[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), (ushort)height);
        return payload;
    }

    private static byte[] BuildWebp(params (string FourCc, byte[] Payload)[] chunks)
    {
        var totalLength = 12;
        foreach (var (_, payload) in chunks)
            totalLength += 8 + payload.Length + (payload.Length & 1);
        var bytes = new byte[totalLength];
        "RIFF"u8.CopyTo(bytes.AsSpan());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        var offset = 12;
        foreach (var (fourCc, payload) in chunks)
        {
            Encoding.ASCII.GetBytes(fourCc).CopyTo(bytes, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4, 4), (uint)payload.Length);
            payload.CopyTo(bytes, offset + 8);
            offset += 8 + payload.Length + (payload.Length & 1);
        }
        return bytes;
    }
}
