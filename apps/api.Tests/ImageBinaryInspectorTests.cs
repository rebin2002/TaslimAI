using System.Buffers.Binary;
using Taslim.Api.Images;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageBinaryInspectorTests
{
    [Fact]
    public void Reads_dimensions_from_vp8l_with_an_odd_sized_chunk_after_metadata()
    {
        var bytes = BuildWebp(
            ("JUNK", [0x01]),
            ("VP8L", BuildVp8L(640, 480)));

        var info = ImageBinaryInspector.Read(bytes);

        Assert.NotNull(info);
        Assert.Equal("image/webp", info.ContentType);
        Assert.Equal("webp", info.Format);
        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
    }

    [Fact]
    public void Reads_dimensions_from_vp8_key_frame()
    {
        var info = ImageBinaryInspector.Read(BuildWebp(("VP8 ", BuildVp8(1536, 1024))));

        Assert.NotNull(info);
        Assert.Equal(1536, info.Width);
        Assert.Equal(1024, info.Height);
    }

    [Fact]
    public void Rejects_malformed_webp_chunks_without_throwing_or_returning_unknown_dimensions()
    {
        var malformed = new List<byte[]>
        {
            BuildWebp(("VP8L", [0x2F])),
            BuildWebp(("VP8 ", new byte[10])),
            BuildWebp(("VP8 ", BuildVp8(0, 480))),
        };
        var oversizedChunk = BuildWebp(("VP8L", BuildVp8L(640, 480)));
        BinaryPrimitives.WriteUInt32LittleEndian(oversizedChunk.AsSpan(16, 4), uint.MaxValue);
        malformed.Add(oversizedChunk);
        var truncatedRiff = BuildWebp(("VP8L", BuildVp8L(640, 480)));
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedRiff.AsSpan(4, 4), 4);
        malformed.Add(truncatedRiff);

        foreach (var bytes in malformed)
            Assert.Null(ImageBinaryInspector.Read(bytes));
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
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        var offset = 12;
        foreach (var (fourCc, payload) in chunks)
        {
            System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(bytes, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4, 4), (uint)payload.Length);
            payload.CopyTo(bytes, offset + 8);
            offset += 8 + payload.Length + (payload.Length & 1);
        }
        return bytes;
    }
}
