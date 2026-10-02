using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
namespace Taslim.Api.Movies;

/// <summary>
/// Concrete neutral adapter for deployments that expose the documented Taslim direct-video
/// contract: POST /generations, GET /generations/{id}, GET /generations/{id}/output, DELETE
/// /generations/{id}. It is inert until the deployment explicitly supplies HTTPS credentials.
/// </summary>
public sealed class DirectVideoHttpAdapter(
    HttpClient client,
    IOptions<DirectVideoProviderOptions> options) : IDirectVideoProviderAdapter
{
    private readonly DirectVideoProviderConfiguration configuration = options.Value.ToConfiguration();
    public string Key => configuration.ProviderKey;
    public bool IsAvailable => configuration.IsConfigured;
    public DirectVideoCapabilityDeclaration Capabilities { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            DirectVideoOperations.TextToVideo,
            DirectVideoOperations.ImageToVideo,
            DirectVideoOperations.VideoToVideo,
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            DirectVideoResolutions.Auto,
            DirectVideoResolutions.Hd720,
            DirectVideoResolutions.FullHd,
            DirectVideoResolutions.Uhd4k,
        },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9", "9:16", "1:1" },
        1, 600, SupportsReferenceImage: true, SupportsContinuation: true,
        SupportsUpscaling: true, SupportsNativeAudio: false);
    public IDirectVideoProviderHealthHook Health => new DirectVideoProviderHealthHook(configuration);

    public async Task<DirectVideoSubmission> SubmitAsync(DirectVideoRequest request, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var payload = new
        {
            request.GenerationJobId,
            request.MovieProjectId,
            request.MovieClipId,
            request.Operation,
            prompt = request.PromptText,
            request.DurationSeconds,
            request.AspectRatio,
            request.Resolution,
            request.UpscaleRequested,
            request.SourceImageUri,
            request.FirstFrameImageUri,
            request.LastFrameImageUri,
            request.ReferenceImages,
            request.ContinuationProviderJobId,
            continuityContext = request.ContinuityContextJson,
        };
        using var response = await SendAsync(HttpMethod.Post, "generations", JsonContent.Create(payload), cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<DirectVideoSubmissionResponse>(cancellationToken: cancellationToken)
            ?? throw new DirectVideoProviderException(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure);
        if (string.IsNullOrWhiteSpace(body.Id) || body.Id.Length > 240)
            throw new DirectVideoProviderException(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure);
        return new DirectVideoSubmission(body.Id.Trim());
    }

    public async Task<DirectVideoStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var id = NormalizeId(providerJobId);
        using var response = await SendAsync(HttpMethod.Get, $"generations/{Uri.EscapeDataString(id)}", null, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<DirectVideoProviderStatusResponse>(cancellationToken: cancellationToken)
            ?? throw new DirectVideoProviderException(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure);
        return DirectVideoResultNormalizer.NormalizeStatus(body);
    }

    public async Task<DirectVideoProviderOutput> RetrieveAsync(string providerJobId, DirectVideoStatus status, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (status.Status != DirectVideoJobStatus.Succeeded)
            throw new DirectVideoProviderException(DirectVideoErrorCategory.InvalidOutput, DirectVideoErrorCodes.OutputInvalid);
        var id = NormalizeId(providerJobId);
        var response = await SendAsync(HttpMethod.Get, $"generations/{Uri.EscapeDataString(id)}/output", null, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? status.ContentType;
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? status.FileName ?? $"video-{id}.mp4";
        var normalized = DirectVideoResultNormalizer.NormalizeOutput(contentType, fileName, bytes.LongLength,
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), status.DurationSeconds,
            status.EstimatedCostUsd, status.ActualCostUsd, status.Currency, status.CostBasis, status.SafeMetadataJson,
            configuration.ModelKey, configuration.MaxOutputBytes);
        return normalized;
    }

    public async Task CancelAsync(string providerJobId, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        using var response = await SendAsync(HttpMethod.Delete, $"generations/{Uri.EscapeDataString(NormalizeId(providerJobId))}", null, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativePath) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetApiKey());
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = DirectVideoErrorNormalizer.FromStatusCode(response.StatusCode);
                response.Dispose();
                throw new DirectVideoProviderException(error.Category, error.Code);
            }
            return response;
        }
        catch (DirectVideoProviderException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DirectVideoProviderException(DirectVideoErrorCategory.TimedOut, DirectVideoErrorCodes.ProviderTimeout); }
        catch (HttpRequestException)
        { throw new DirectVideoProviderException(DirectVideoErrorCategory.Unavailable, DirectVideoErrorCodes.ProviderUnavailable); }
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable) throw new DirectVideoProviderException(DirectVideoErrorCategory.Unavailable, DirectVideoErrorCodes.ProviderUnavailable);
    }
    private string GetApiKey() => options.Value.ApiKey.Trim();
    private static string NormalizeId(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 240 ? value.Trim() : throw new DirectVideoProviderException(DirectVideoErrorCategory.InvalidRequest, DirectVideoErrorCodes.RequestInvalid);
    private sealed record DirectVideoSubmissionResponse(string? Id);
}
