using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Taslim.Api.Autopilot;

/// <summary>
/// Wave-launch provider backed by the existing, already-deployed Taslim
/// execution bridge (<c>POST /v1/tasks</c>, <c>GET /v1/tasks/{id}</c>). The
/// controller does not depend on the bridge repository, only on this published
/// HTTP contract.
///
/// Safety properties:
/// <list type="bullet">
/// <item>the bearer token is read from a server-side environment variable only and is never persisted, logged, or returned;</item>
/// <item>a malformed, ambiguous, or non-2xx response is reported as
/// <see cref="WaveLaunchProviderOutcomes.Unavailable"/> or
/// <see cref="WaveLaunchProviderOutcomes.Rejected"/> — never as a successful launch;</item>
/// <item>status polling only ever reports a normalized execution state; it never
/// produces gate evidence.</item>
/// </list>
/// </summary>
public sealed class ManusBridgeWaveLaunchProvider : IWaveLaunchProvider
{
    public const string Key = "manus-bridge";

    /// <summary>Bounded size of an accepted bridge response body.</summary>
    private const int MaxResponseCharacters = 100_000;

    private readonly HttpClient http;
    private readonly AutopilotOptions settings;

    public ManusBridgeWaveLaunchProvider(HttpClient http, AutopilotOptions settings)
    {
        this.http = http;
        this.settings = settings;
        this.http.Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.BridgeTimeoutSeconds, 5, 120));
    }

    public string ProviderKey => Key;

    public bool IsConfigured =>
        TryResolveBaseUri(out _) && !string.IsNullOrWhiteSpace(ResolveToken());

    public async Task<WaveLaunchProviderResult> LaunchTaskAsync(WaveLaunchProviderRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryResolveBaseUri(out var baseUri))
            return WaveLaunchProviderResult.Unavailable("bridge_not_configured", "No bridge base URL is configured.");

        var token = ResolveToken();
        if (string.IsNullOrWhiteSpace(token))
            return WaveLaunchProviderResult.Unavailable("bridge_credential_missing", "The bridge credential environment variable is empty.");

        var payload = JsonSerializer.Serialize(new
        {
            waveKey = request.WaveKey,
            taskKey = request.TaskKey,
            title = request.Title,
            instructions = request.Instructions,
            baseSha = request.BaseSha,
            branch = request.Branch,
            idempotencyKey = request.IdempotencyKey,
        });

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, settings.BridgeCreateTaskPath))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var code = $"bridge_status_{(int)response.StatusCode}";
                return IsTransient(response.StatusCode)
                    ? WaveLaunchProviderResult.Unavailable(code, "The bridge reported a transient failure.")
                    : WaveLaunchProviderResult.Rejected(code, "The bridge refused the task request.");
            }

            var body = await ReadBoundedAsync(response, cancellationToken);
            var externalTaskId = ExtractExternalTaskId(body);
            if (string.IsNullOrWhiteSpace(externalTaskId))
                return WaveLaunchProviderResult.Unavailable("bridge_task_identity_missing", "The bridge response did not contain a task identity.");

            return WaveLaunchProviderResult.Launched(externalTaskId, "task_accepted");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WaveLaunchProviderResult.Unavailable("bridge_timeout", "The bridge call timed out.");
        }
        catch (HttpRequestException)
        {
            return WaveLaunchProviderResult.Unavailable("bridge_unreachable", "The bridge could not be reached.");
        }
        catch (JsonException)
        {
            return WaveLaunchProviderResult.Unavailable("bridge_response_unreadable", "The bridge response could not be parsed.");
        }
    }

    public async Task<WaveLaunchTaskState?> GetTaskStateAsync(string externalTaskId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalTaskId)) return null;
        if (!TryResolveBaseUri(out var baseUri)) return null;

        var token = ResolveToken();
        if (string.IsNullOrWhiteSpace(token)) return null;

        var path = settings.BridgeGetTaskPathTemplate.Replace("{taskId}", Uri.EscapeDataString(externalTaskId), StringComparison.Ordinal);
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var body = await ReadBoundedAsync(response, cancellationToken);
            return ParseTaskState(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a bridge task payload into a provider-neutral state. The
    /// mapping is deliberately conservative: anything that is not explicitly
    /// recognizable as a terminal success or a terminal failure is reported as
    /// non-terminal (or unknown), never as success.
    /// </summary>
    public static WaveLaunchTaskState? ParseTaskState(string? body)
    {
        var status = ExtractStatusToken(body);
        if (string.IsNullOrWhiteSpace(status)) return null;

        var normalized = NormalizeStatus(status);
        var isTerminal = WaveLaunchTaskStates.TerminalStates.Contains(normalized);
        var isSuccess = WaveLaunchTaskStates.SucceededStates.Contains(normalized);
        return new WaveLaunchTaskState(
            normalized,
            isTerminal,
            isSuccess,
            ExtractBoundedReference(body),
            $"bridge_status={Bounded(status.ToLowerInvariant(), 40)}");
    }

    public static string NormalizeStatus(string? status)
    {
        var token = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (token.Length == 0) return WaveLaunchTaskStates.Unknown;

        return token switch
        {
            "completed" or "complete" or "succeeded" or "success" or "done" or "finished" or "merged" or "passed"
                => WaveLaunchTaskStates.Succeeded,
            "failed" or "failure" or "error" or "errored" or "timed_out" or "timeout"
                => WaveLaunchTaskStates.Failed,
            "cancelled" or "canceled" or "stopped" or "aborted" or "killed" or "terminated"
                => WaveLaunchTaskStates.Cancelled,
            "blocked" or "awaiting_input" or "awaiting_approval" or "needs_input" or "requires_human"
                => WaveLaunchTaskStates.Blocked,
            "queued" or "pending" or "created" or "scheduled" or "accepted"
                => WaveLaunchTaskStates.Pending,
            "running" or "in_progress" or "in-progress" or "processing" or "started" or "active"
                => WaveLaunchTaskStates.Running,
            _ => WaveLaunchTaskStates.Unknown,
        };
    }

    /// <summary>
    /// Resolves the external task identity from a tolerant set of response
    /// shapes so a small bridge response evolution cannot break the controller.
    /// </summary>
    public static string? ExtractExternalTaskId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var candidate = FirstString(root, "taskId", "task_id", "id", "taskID")
            ?? NestedString(root, "task", "id", "taskId", "task_id")
            ?? NestedString(root, "data", "id", "taskId", "task_id")
            ?? NestedString(root, "result", "id", "taskId", "task_id")
            ?? NestedString(root, "result", "task", "id");

        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var trimmed = candidate.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    private static string? ExtractStatusToken(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return FirstString(root, "status", "state", "taskStatus", "task_status")
            ?? NestedString(root, "task", "status", "state")
            ?? NestedString(root, "data", "status", "state");
    }

    private static string? ExtractBoundedReference(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var candidate = FirstString(root, "updatedAt", "completedAt", "finishedAt", "candidateSha", "headSha", "commit")
            ?? NestedString(root, "result", "candidateSha", "sha", "commit");
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var trimmed = candidate.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            var resolved = AsString(value);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
        }
        return null;
    }

    private static string? NestedString(JsonElement element, string parent, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(parent, out var child)) return null;
        if (child.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in child.EnumerateArray())
            {
                var resolved = FirstString(item, names);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
            }
            return null;
        }
        return FirstString(child, names);
    }

    private static string? AsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.ToString(),
        _ => null,
    };

    private static bool IsTransient(System.Net.HttpStatusCode status) =>
        (int)status >= 500 || status == System.Net.HttpStatusCode.RequestTimeout
        || status == System.Net.HttpStatusCode.TooManyRequests;

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return body.Length <= MaxResponseCharacters ? body : body[..MaxResponseCharacters];
    }

    private bool TryResolveBaseUri(out Uri baseUri)
    {
        baseUri = null!;
        var candidate = (settings.BridgeBaseUrl ?? string.Empty).Trim();
        if (candidate.Length == 0) return false;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        baseUri = parsed;
        return true;
    }

    private string ResolveToken() =>
        Environment.GetEnvironmentVariable(settings.BridgeTokenEnvironmentVariable) ?? string.Empty;

    private static string Bounded(string value, int max) => value.Length <= max ? value : value[..max];
}
