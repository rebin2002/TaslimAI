using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;
using FileSettings = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Tests;

public sealed class FileUploadSizeLimitTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public FileUploadSizeLimitTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Normal_text_file_at_the_limit_is_accepted_and_one_byte_over_is_rejected()
    {
        var maxFileSize = FileSettings.MaxUserUploadFileSizeBytes;
        var content = new byte[checked((int)maxFileSize + 1)];
        Array.Fill(content, (byte)'a');
        var validator = new FileValidationService(Options.Create(new FileSettings()));

        var accepted = CreateTextFile(content, maxFileSize);
        var result = await validator.ValidateAsync(accepted);

        Assert.Equal(maxFileSize, result.SizeBytes);
        Assert.Equal("accepted.txt", result.SafeFileName);

        var oversized = CreateTextFile(content, maxFileSize + 1);
        var exception = await Assert.ThrowsAsync<FileUploadValidationException>(() => validator.ValidateAsync(oversized));

        Assert.Equal("Files must be 25 MB or smaller.", exception.Message);
    }

    [Fact]
    public async Task Upload_route_rejects_multipart_request_over_aggregate_envelope_before_form_binding()
    {
        var maxFileSize = FileSettings.MaxUserUploadFileSizeBytes;
        var maxRequestBodyBytes = maxFileSize + FileSettings.MultipartFormOverheadBytes;
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[checked((int)maxFileSize)]);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(file, "file", "within-file-limit.txt");
        multipart.Add(new StringContent(new string('x', checked((int)FileSettings.MultipartFormOverheadBytes))), "extra");

        var body = await multipart.ReadAsByteArrayAsync();
        Assert.True(body.LongLength > maxRequestBodyBytes, "Multipart framing must put the whole request over the cap.");

        using var requestContent = new ByteArrayContent(body);
        requestContent.Headers.ContentType = multipart.Headers.ContentType;
        using var client = factory.CreateClient();
        using var response = await client.PostAsync($"/api/workspaces/{Guid.NewGuid()}/files", requestContent);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Upload_route_accepts_a_compliant_multipart_request()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var csrf = await GetCsrfToken(client);
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent("Compliant upload"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(file, "file", "accepted.txt");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/files")
        {
            Content = multipart,
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(26214400, true)]
    [InlineData(26214401, false)]
    public void Configured_maximum_stays_within_the_supported_upload_envelope(long maxFileSizeBytes, bool isValid)
    {
        Assert.Equal(isValid, FileSettings.IsValidMaxFileSizeBytes(maxFileSizeBytes));
    }

    private static FormFile CreateTextFile(byte[] content, long length) => new(new MemoryStream(content, writable: false), 0, length, "file", "accepted.txt")
    {
        Headers = new HeaderDictionary(),
        ContentType = "text/plain",
    };

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var csrf = await GetCsrfToken(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Upload Limit Owner",
                email = $"upload-limit-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<string> GetCsrfToken(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return csrf.GetProperty("token").GetString()!;
    }
}
