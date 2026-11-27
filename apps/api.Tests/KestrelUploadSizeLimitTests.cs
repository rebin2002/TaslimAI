using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;
using Xunit;
using FileSettings = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Tests;

public sealed class KestrelUploadSizeLimitTests
{
    [Fact]
    public async Task Unknown_length_streamed_overrun_returns_413_while_genuine_antiforgery_failures_remain_400()
    {
        var maxRequestBodyBytes = FileSettings.MaxUserUploadFileSizeBytes + FileSettings.MultipartFormOverheadBytes;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxRequestBodyBytes);

        await using var app = builder.Build();
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.StartsWithSegments("/api/workspaces")
                && context.Request.Path.Value?.EndsWith("/files", StringComparison.Ordinal) == true)
            {
                var requestBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (requestBodySizeFeature is { IsReadOnly: false })
                    requestBodySizeFeature.MaxRequestBodySize = maxRequestBodyBytes;
            }

            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "kestrel-upload-limit-test")],
                authenticationType: "test"));
            await next();
        });
        app.UseMiddleware<AntiforgeryValidationMiddleware>();
        app.MapPost("/api/workspaces/{workspaceId:guid}/files", async context =>
        {
            await context.Request.ReadFormAsync();
            context.Response.StatusCode = StatusCodes.Status201Created;
        });

        await app.StartAsync();
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var overLimitContent = new UnknownLengthMultipartContent(maxRequestBodyBytes + 1);
            using var overLimitRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{Guid.NewGuid()}/files")
            {
                Content = overLimitContent,
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            overLimitRequest.Headers.TransferEncodingChunked = true;
            Assert.Null(overLimitRequest.Content.Headers.ContentLength);

            using var overLimitResponse = await client.SendAsync(overLimitRequest);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overLimitResponse.StatusCode);
            var overLimitBody = await overLimitResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("CSRF_VALIDATION_FAILED", overLimitBody, StringComparison.Ordinal);

            using var underLimitContent = new StringContent("small body");
            underLimitContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            using var invalidAntiforgeryRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{Guid.NewGuid()}/files")
            {
                Content = underLimitContent,
            };

            using var invalidAntiforgeryResponse = await client.SendAsync(invalidAntiforgeryRequest);

            Assert.Equal(HttpStatusCode.BadRequest, invalidAntiforgeryResponse.StatusCode);
            using var invalidAntiforgeryBody = JsonDocument.Parse(await invalidAntiforgeryResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                "CSRF_VALIDATION_FAILED",
                invalidAntiforgeryBody.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class UnknownLengthMultipartContent : HttpContent
    {
        private const string Boundary = "taslim-kestrel-upload-limit-boundary";
        private const int ChunkSize = 32 * 1024;
        private static readonly byte[] Chunk = CreateChunk();
        private static readonly byte[] Prefix = Encoding.ASCII.GetBytes(
            $"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"oversized.txt\"\r\nContent-Type: text/plain\r\n\r\n");
        private static readonly byte[] Suffix = Encoding.ASCII.GetBytes($"\r\n--{Boundary}--\r\n");
        private readonly long fileLength;

        public UnknownLengthMultipartContent(long fileLength)
        {
            this.fileLength = fileLength;
            Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
            Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", Boundary));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Prefix);
            var remaining = fileLength;
            while (remaining > 0)
            {
                var count = (int)Math.Min(Chunk.Length, remaining);
                await stream.WriteAsync(Chunk.AsMemory(0, count));
                remaining -= count;
            }

            await stream.WriteAsync(Suffix);
        }

        private static byte[] CreateChunk()
        {
            var chunk = new byte[ChunkSize];
            Array.Fill(chunk, (byte)'x');
            return chunk;
        }
    }
}
