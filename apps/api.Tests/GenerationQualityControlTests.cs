using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GenerationQualityControlUnitTests
{
    private readonly GenerationQualityControlService quality = new(
        Options.Create(new GenerationQualityControlOptions()),
        NullLogger<GenerationQualityControlService>.Instance);

    [Fact]
    public async Task Valid_png_passes_with_dimensions()
    {
        var result = await ValidateAsync(AssetTypes.Image, "image/png", Png(320, 180));

        Assert.Equal(GenerationQualityOutcome.Passed, result.Outcome);
        Assert.Equal(320, result.Width);
        Assert.Equal(180, result.Height);
    }

    [Fact]
    public async Task Zero_byte_output_fails_permanently()
    {
        var result = await ValidateAsync(AssetTypes.Image, "image/png", []);

        Assert.Equal(GenerationQualityOutcome.FailedPermanent, result.Outcome);
        Assert.Equal(GenerationQualityReasonCodes.NonEmpty, result.PrimaryReasonCode);
    }

    [Fact]
    public async Task Incorrect_mime_fails_permanently()
    {
        var result = await ValidateAsync(AssetTypes.Image, "application/octet-stream", Png(10, 10));

        Assert.Equal(GenerationQualityOutcome.FailedPermanent, result.Outcome);
        Assert.Equal(GenerationQualityReasonCodes.MimeUnrecognized, result.PrimaryReasonCode);
    }

    [Fact]
    public async Task Oversized_output_fails_permanently()
    {
        var service = new GenerationQualityControlService(
            Options.Create(new GenerationQualityControlOptions { MaxImageBytes = 8 }),
            NullLogger<GenerationQualityControlService>.Instance);

        var result = await service.ValidateAsync(Job(), new GenerationHandlerOutput(
            GenerationJobOutputTypes.StoredFile,
            null,
            null,
            new GeneratedFileArtifact("image.png", "image/png", Png(10, 10)),
            new GeneratedAssetDescriptor("image", null, AssetTypes.Image)));

        Assert.Equal(GenerationQualityOutcome.FailedPermanent, result.Outcome);
        Assert.Equal(GenerationQualityReasonCodes.SizeExceeded, result.PrimaryReasonCode);
    }

    [Fact]
    public async Task Corrupt_representation_fails_permanently()
    {
        var result = await ValidateAsync(AssetTypes.Image, "image/png", "not-an-image"u8.ToArray());

        Assert.Equal(GenerationQualityOutcome.FailedPermanent, result.Outcome);
        Assert.Equal(GenerationQualityReasonCodes.RepresentationCorrupt, result.PrimaryReasonCode);
    }

    [Fact]
    public async Task Valid_audio_container_passes_and_keeps_available_duration()
    {
        var result = await quality.ValidateAsync(Job(), new GenerationHandlerOutput(
            GenerationJobOutputTypes.StoredFile,
            null,
            "{\"durationSeconds\":1.25}",
            new GeneratedFileArtifact("voice.wav", "audio/wav", Wav()),
            new GeneratedAssetDescriptor("voice", null, AssetTypes.Audio)));

        Assert.Equal(GenerationQualityOutcome.Passed, result.Outcome);
        Assert.Equal(1.25, result.DurationSeconds);
    }

    [Fact]
    public async Task Recognized_video_container_passes_deterministic_signature_check()
    {
        var result = await ValidateAsync(AssetTypes.Video, "video/mp4", Mp4Ftyp());

        Assert.Equal(GenerationQualityOutcome.Passed, result.Outcome);
    }

    [Fact]
    public async Task Truncated_video_is_retryable_but_empty_video_is_permanent()
    {
        var truncated = await ValidateAsync(AssetTypes.Video, "video/mp4", "not-an-mp4"u8.ToArray());
        var empty = await ValidateAsync(AssetTypes.Video, "video/mp4", []);

        Assert.Equal(GenerationQualityOutcome.FailedRetryable, truncated.Outcome);
        Assert.Equal(GenerationQualityReasonCodes.RepresentationTruncated, truncated.PrimaryReasonCode);
        Assert.Equal(GenerationQualityOutcome.FailedPermanent, empty.Outcome);
        Assert.Equal(GenerationQualityReasonCodes.NonEmpty, empty.PrimaryReasonCode);
    }

    [Fact]
    public async Task Valid_json_remains_passed_by_structured_validation()
    {
        var result = await ValidateAsync(AssetTypes.File, "application/json", "{\"ok\":true}"u8.ToArray());

        Assert.Equal(GenerationQualityOutcome.Passed, result.Outcome);
    }

    private async Task<GenerationQualityControlResult> ValidateAsync(string assetType, string contentType, byte[] content) =>
        await quality.ValidateAsync(Job(), new GenerationHandlerOutput(
            GenerationJobOutputTypes.StoredFile,
            null,
            null,
            new GeneratedFileArtifact("output.bin", contentType, content),
            new GeneratedAssetDescriptor("output", null, assetType)));

    private static GenerationJob Job() => new()
    {
        Id = Guid.NewGuid(),
        JobType = GenerationJobTypes.SystemTest,
    };

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[40];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        "IEND"u8.CopyTo(bytes.AsSpan(32, 4));
        return bytes;
    }

    private static byte[] Wav()
    {
        var bytes = new byte[44];
        "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 36);
        "WAVE"u8.CopyTo(bytes.AsSpan(8, 4));
        "fmt "u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 8_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 8_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 8);
        "data"u8.CopyTo(bytes.AsSpan(36, 4));
        return bytes;
    }

    private static byte[] Mp4Ftyp()
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 24);
        "ftyp"u8.CopyTo(bytes.AsSpan(4, 4));
        "isom"u8.CopyTo(bytes.AsSpan(8, 4));
        "isom"u8.CopyTo(bytes.AsSpan(16, 4));
        return bytes;
    }
}

public sealed class GenerationQualityControlApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGenerationJobHandler>();
            services.AddSingleton<IGenerationJobHandler, InvalidQualityGenerationJobHandler>();
        });
    }
}

public sealed class GenerationQualityControlIntegrationTests : IClassFixture<GenerationQualityControlApiFactory>
{
    private readonly GenerationQualityControlApiFactory factory;

    public GenerationQualityControlIntegrationTests(GenerationQualityControlApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Qc_failure_never_creates_asset_and_finalizes_usage_once_with_safe_error()
    {
        using var client = factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        register.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        register.Content = JsonContent.Create(new { displayName = "QC Tester", email = $"qc-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        var authResponse = await client.SendAsync(register);
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var auth = (await authResponse.Content.ReadFromJsonAsync<AuthResponse>())!;

        csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/generation/jobs");
        create.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        create.Content = JsonContent.Create(new { workspaceId = auth.PersonalWorkspace.Id, jobType = GenerationJobTypes.SystemTest, inputJson = "{}" });
        var createResponse = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<GenerationJobDto>())!;

        GenerationJobDto? terminal = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            terminal = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Id}");
            if (terminal?.Status is "Succeeded" or "Failed" or "Cancelled") break;
            await Task.Delay(50);
        }

        Assert.NotNull(terminal);
        Assert.Equal("Failed", terminal!.Status);
        Assert.Equal(GenerationJobErrorCodes.ExecutionFailed, terminal.ErrorCode);
        Assert.Equal("The job could not be completed.", terminal.ErrorMessage);
        Assert.Empty(terminal.Outputs);
        Assert.DoesNotContain("QUALITY_OUTPUT", terminal.ErrorMessage ?? string.Empty, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Id));
        Assert.False(await db.StoredFiles.AsNoTracking().AnyAsync(item => item.GenerationJobOutputs.Any(output => output.GenerationJobId == created.Id)));
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Id);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Contains(GenerationQualityReasonCodes.RepresentationCorrupt, usage.SafeMetadataJson, StringComparison.Ordinal);
        Assert.Contains("FailedPermanent", usage.SafeMetadataJson, StringComparison.Ordinal);
    }
}

internal sealed class InvalidQualityGenerationJobHandler : IGenerationJobHandler
{
    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.SystemTest, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        progress.Report(100);
        await Task.Yield();
        var artifact = new GeneratedFileArtifact("invalid.png", "image/png", "not-an-image"u8.ToArray());
        var asset = new GeneratedAssetDescriptor("Invalid output", null, AssetTypes.Image);
        return new GenerationHandlerResult(
            "{}",
            [new GenerationHandlerOutput(GenerationJobOutputTypes.StoredFile, null, null, artifact, asset)],
            new AiUsageMetadata("test", "test", null, null, null, 0m, 0m, 1, "completed", true));
    }
}
