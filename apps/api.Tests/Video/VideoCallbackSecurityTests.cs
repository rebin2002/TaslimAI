using System.Text;
using Taslim.Api.Video;
using Xunit;

namespace Taslim.Api.Tests.Video;

public sealed class VideoCallbackSecurityTests
{
    [Fact]
    public void Valid_signature_is_accepted_without_exposing_adapter_details()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"completed\"}");
        var secret = Encoding.UTF8.GetBytes("test-only-secret");
        var receivedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var timestamp = receivedAt.ToUnixTimeSeconds().ToString();
        var signature = VideoCallbackSecurity.ComputeHmacSha256(body, secret, timestamp);

        Assert.True(VideoCallbackSecurity.IsAuthentic(body, secret, timestamp, signature, receivedAt, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Tampering_and_replay_window_fail_closed()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var secret = Encoding.UTF8.GetBytes("test-only-secret");
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var timestamp = signedAt.ToUnixTimeSeconds().ToString();
        var signature = VideoCallbackSecurity.ComputeHmacSha256(body, secret, timestamp);

        Assert.False(VideoCallbackSecurity.IsAuthentic(Encoding.UTF8.GetBytes("tampered"), secret, timestamp, signature, signedAt, TimeSpan.FromMinutes(5)));
        Assert.False(VideoCallbackSecurity.IsAuthentic(body, secret, timestamp, signature, signedAt.AddMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.False(VideoCallbackSecurity.IsAuthentic(body, secret, timestamp, signature, signedAt, TimeSpan.Zero));
    }
}
