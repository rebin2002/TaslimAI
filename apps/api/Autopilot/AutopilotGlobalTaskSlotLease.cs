using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

/// <summary>
/// Collision-resistant stable identity shared by slot reservation and provider launch.
/// Length-prefixing keeps the (wave, task) tuple unambiguous before hashing.
/// </summary>
public static class AutopilotGlobalTaskSlotIdentity
{
    public const string IdempotencyKeyPrefix = "autopilot:v2:";

    public static string CreateIdempotencyKey(string waveKey, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(waveKey)) throw new ArgumentException("Wave key is required.", nameof(waveKey));
        if (string.IsNullOrWhiteSpace(taskKey)) throw new ArgumentException("Task key is required.", nameof(taskKey));

        var waveBytes = System.Text.Encoding.UTF8.GetBytes(waveKey);
        var taskBytes = System.Text.Encoding.UTF8.GetBytes(taskKey);
        var material = new byte[sizeof(int) + waveBytes.Length + sizeof(int) + taskBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(material.AsSpan(0, sizeof(int)), waveBytes.Length);
        waveBytes.AsSpan().CopyTo(material.AsSpan(sizeof(int)));
        var taskLengthOffset = sizeof(int) + waveBytes.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(material.AsSpan(taskLengthOffset, sizeof(int)), taskBytes.Length);
        taskBytes.AsSpan().CopyTo(material.AsSpan(taskLengthOffset + sizeof(int)));
        var digest = System.Security.Cryptography.SHA256.HashData(material);
        return IdempotencyKeyPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }
}

/// <summary>
/// One entry from an authoritative inventory universe. ScopeId identifies the
/// coverage universe, not task ownership: the Bridge user-wide source deliberately
/// includes every visible task (including unrelated creators and UI tasks).
/// </summary>
public sealed record AutopilotGlobalTaskInventoryItem(
    string TaskId,
    string ScopeId,
    string Status,
    string? IdempotencyKey);

/// <summary>
/// Versioned receipt embedded in the signed completion signal's evidence field.
/// The producer must obtain it from an authoritative task.detail read; this
/// allocator only validates the persisted, signature-verified receipt and does
/// not fetch provider details itself. The receipt event_id must exactly match
/// the persisted event's ExternalEventId before a slot can be released.
/// </summary>
public sealed record AutopilotGlobalTaskDetailTerminalReceipt(
    [property: System.Text.Json.Serialization.JsonPropertyName("kind")] string? Kind,
    [property: System.Text.Json.Serialization.JsonPropertyName("task_id")] string? TaskId,
    [property: System.Text.Json.Serialization.JsonPropertyName("agent_status")] string? AgentStatus,
    [property: System.Text.Json.Serialization.JsonPropertyName("stop_reason")] string? StopReason,
    [property: System.Text.Json.Serialization.JsonPropertyName("has_running_background_jobs")] bool? HasRunningBackgroundJobs,
    [property: System.Text.Json.Serialization.JsonPropertyName("event_id")] string? EventId)
{
    public const string RequiredKind = "taslim.manus.task_detail_terminal_receipt.v1";
}

/// <summary>A single page from the external inventory endpoint.</summary>
public sealed record AutopilotGlobalTaskInventoryPage(
    string QueryScopeId,
    IReadOnlyList<AutopilotGlobalTaskInventoryItem>? Items,
    string? NextCursor);

/// <summary>Page-source contract; implementations must request the exact supplied scope.</summary>
public interface IAutopilotGlobalTaskInventoryPageSource
{
    Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
        string scopeId,
        string? cursor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit scope label for the optional Bridge inventory adapter. This is one
/// authenticated Manus user's visible task universe, not Taslim-only and not a
/// team-wide inventory. The source is intentionally not registered in DI.
/// </summary>
public static class AutopilotGlobalTaskInventoryScopeIds
{
    public const string ManusBridgeAuthenticatedUserWide = "manus-bridge:authenticated-user-wide";
}

/// <summary>Credential identity used to restrict Manus task.list to one creator.</summary>
public enum AutopilotManusTaskListCredentialKind
{
    ApiKey,
    OAuthBearer,
}

/// <summary>
/// Reads one explicitly creator-scoped page from the official Manus v2 task.list API.
/// This adapter requires a dedicated Taslim API-key ID or OAuth-client ID, an explicit
/// source-backed identity attestation, and an injected credential provider. It deliberately
/// has no unscoped-list mode and does not read environment variables or register itself in DI.
/// </summary>
public sealed class ManusTaskListInventoryPageSource : IAutopilotGlobalTaskInventoryPageSource, IDisposable
{
    public const int PageSize = 100;
    private const int MaximumResponseBytes = 1_000_000;
    private const int MaximumCursorLength = 2_048;
    private static readonly TimeSpan DefaultPageTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumPageTimeout = TimeSpan.FromSeconds(30);
    private static readonly Uri TaskListEndpoint = new("https://api.manus.ai/v2/task.list", UriKind.Absolute);

    private readonly HttpClient http;
    private readonly Func<string?> credentialProvider;
    private readonly string taslimScopeId;
    private readonly string creatorId;
    private readonly string creatorFilterName;
    private readonly bool creatorIdentityVerified;
    private readonly TimeSpan pageTimeout;

    /// <param name="creatorIdentityVerified">True only after the exact creator ID is independently verified from the Taslim Bridge source/configuration.</param>
    public ManusTaskListInventoryPageSource(
        Func<string?> credentialProvider,
        string taslimScopeId,
        string? creatorId,
        bool creatorIdentityVerified,
        AutopilotManusTaskListCredentialKind credentialKind,
        TimeSpan? pageTimeout = null,
        HttpMessageHandler? testHandler = null)
    {
        this.credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        this.taslimScopeId = string.IsNullOrWhiteSpace(taslimScopeId)
            ? throw new ArgumentException("A stable Taslim inventory scope is required.", nameof(taslimScopeId))
            : taslimScopeId.Trim();
        this.creatorId = creatorId?.Trim() ?? string.Empty;
        this.creatorIdentityVerified = creatorIdentityVerified
            && !string.IsNullOrWhiteSpace(this.creatorId)
            && this.creatorId.Length <= 200;
        this.pageTimeout = pageTimeout ?? DefaultPageTimeout;
        if (this.pageTimeout <= TimeSpan.Zero || this.pageTimeout > MaximumPageTimeout)
            throw new ArgumentOutOfRangeException(nameof(pageTimeout), $"Page timeout must be positive and at most {MaximumPageTimeout}.");

        // The production default refuses redirects, so x-manus-api-key cannot be
        // forwarded to another origin. Tests inject a deterministic fake handler only.
        http = new HttpClient(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        creatorFilterName = credentialKind switch
        {
            AutopilotManusTaskListCredentialKind.ApiKey => "api_key_id",
            AutopilotManusTaskListCredentialKind.OAuthBearer => "oauth_client_id",
            _ => throw new ArgumentOutOfRangeException(nameof(credentialKind)),
        };
    }

    public async Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
        string scopeId,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        // Manus returns an empty successful list for an unknown creator ID. Do not
        // convert that response into zero occupancy unless the exact ID was verified
        // against the Taslim Bridge source/configuration by the integrator.
        if (!creatorIdentityVerified
            || !string.Equals(scopeId, taslimScopeId, StringComparison.Ordinal)
            || (cursor is not null && (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaximumCursorLength)))
            return null;

        var credential = credentialProvider()?.Trim();
        if (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsWhiteSpace))
            return null;

        var query = new List<string>
        {
            "limit=100",
            "order=desc",
            "scope=all",
            $"{creatorFilterName}={Uri.EscapeDataString(creatorId)}",
        };
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        var requestUri = new UriBuilder(TaskListEndpoint) { Query = string.Join('&', query) }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (creatorFilterName == "api_key_id")
            request.Headers.TryAddWithoutValidation("x-manus-api-key", credential);
        else
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(pageTimeout);
        var pageToken = deadline.Token;

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pageToken);
            if (!response.IsSuccessStatusCode) return null;
            var payload = await ReadBoundedAsync(response.Content, pageToken);
            if (payload is null) return null;

            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind != System.Text.Json.JsonValueKind.True
                || !root.TryGetProperty("data", out var tasks)
                || tasks.ValueKind != System.Text.Json.JsonValueKind.Array
                || !root.TryGetProperty("has_more", out var hasMoreElement)
                || (hasMoreElement.ValueKind != System.Text.Json.JsonValueKind.True
                    && hasMoreElement.ValueKind != System.Text.Json.JsonValueKind.False))
                return null;

            var hasMore = hasMoreElement.GetBoolean();
            string? nextCursor = null;
            if (root.TryGetProperty("next_cursor", out var cursorElement)
                && cursorElement.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                if (cursorElement.ValueKind != System.Text.Json.JsonValueKind.String) return null;
                nextCursor = cursorElement.GetString();
            }

            if (hasMore != (nextCursor is not null)
                || (nextCursor is not null && (string.IsNullOrWhiteSpace(nextCursor) || nextCursor.Length > MaximumCursorLength)))
                return null;

            var items = new List<AutopilotGlobalTaskInventoryItem>();
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !task.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(idElement.GetString())
                    || !task.TryGetProperty("status", out var statusElement)
                    || statusElement.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(statusElement.GetString()))
                    return null;

                bool? hasRunningBackgroundJobs = null;
                if (task.TryGetProperty("has_running_background_jobs", out var backgroundElement))
                {
                    if (backgroundElement.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                        return null;
                    hasRunningBackgroundJobs = backgroundElement.GetBoolean();
                }

                var taskId = idElement.GetString()!.Trim();
                var status = ConservativelyNormalizeStatus(statusElement.GetString()!, hasRunningBackgroundJobs);
                items.Add(new AutopilotGlobalTaskInventoryItem(taskId, taslimScopeId, status, null));
            }

            return new AutopilotGlobalTaskInventoryPage(taslimScopeId, items, nextCursor);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Dispose() => http.Dispose();

    private static string ConservativelyNormalizeStatus(string status, bool? hasRunningBackgroundJobs)
    {
        var normalized = status.Trim().ToLowerInvariant();
        if (hasRunningBackgroundJobs == true) return "running";
        if (normalized is "waiting" or "stopped") return normalized;
        if (normalized == "error" && hasRunningBackgroundJobs is null) return "unknown";
        return normalized;
    }

    internal static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken,
        int maximumResponseBytes = MaximumResponseBytes)
    {
        if (content.Headers.ContentLength > maximumResponseBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maximumResponseBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>
/// Optional page source for Bridge's aggregated GET /v1/tasks endpoint. It reads
/// the complete inventory visible to the Bridge's authenticated Manus API-key user,
/// including UI tasks and tasks from unrelated API keys, and counts those entries
/// conservatively. It cannot establish team-wide visibility or attribute tasks to
/// Taslim. A caller must use ManusBridgeAuthenticatedUserWide as the inventory
/// scope; Taslim-only or team-wide scope labels fail closed. This adapter is not
/// registered in DI pending source-backed deployment credential binding. Its Bridge
/// response must include `manus_user_id` obtained from official user.me using the
/// same Manus API key used by task.list and task.create, and that value must match
/// the explicitly configured expected Manus user ID.
/// </summary>
public sealed class ManusBridgeUserWideTaskInventoryPageSource : IAutopilotGlobalTaskInventoryPageSource, IDisposable
{
    public const int MaximumAggregateResponseBytes = 16 * 1024 * 1024;
    public const int MaximumTasks = 10_000;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromSeconds(120);
    private const string RequiredTasksPath = "/v1/tasks";

    private readonly HttpClient http;
    private readonly Func<string?> bridgeTokenProvider;
    private readonly Uri tasksEndpoint;
    private readonly string expectedManusUserId;
    private readonly TimeSpan requestTimeout;

    public ManusBridgeUserWideTaskInventoryPageSource(
        Func<string?> bridgeTokenProvider,
        Uri tasksEndpoint,
        string expectedManusUserId,
        TimeSpan? requestTimeout = null,
        HttpMessageHandler? testHandler = null)
    {
        this.bridgeTokenProvider = bridgeTokenProvider ?? throw new ArgumentNullException(nameof(bridgeTokenProvider));
        if (string.IsNullOrWhiteSpace(expectedManusUserId)
            || expectedManusUserId.Length > 256
            || expectedManusUserId.Any(char.IsWhiteSpace)
            || expectedManusUserId.Any(char.IsControl))
            throw new ArgumentException("Expected Manus user ID must be explicitly configured and contain no whitespace or control characters.", nameof(expectedManusUserId));

        this.expectedManusUserId = expectedManusUserId;
        if (tasksEndpoint is null
            || !tasksEndpoint.IsAbsoluteUri
            || !string.Equals(tasksEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tasksEndpoint.AbsolutePath, RequiredTasksPath, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(tasksEndpoint.Query)
            || !string.IsNullOrEmpty(tasksEndpoint.Fragment)
            || !string.IsNullOrEmpty(tasksEndpoint.UserInfo))
            throw new ArgumentException("Bridge inventory endpoint must be HTTPS /v1/tasks with no query, fragment, or user-info.", nameof(tasksEndpoint));

        this.tasksEndpoint = tasksEndpoint;
        this.requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (this.requestTimeout <= TimeSpan.Zero || this.requestTimeout > MaximumRequestTimeout)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), $"Request timeout must be positive and at most {MaximumRequestTimeout}.");

        // Do not forward the Bridge bearer token through redirects.
        http = new HttpClient(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
        string scopeId,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        // Bridge aggregates its own cursor traversal and rejects query parameters.
        // Refuse Taslim-only/team labels rather than claiming the full response was
        // creator-filtered or visible across all Manus users.
        if (!string.Equals(
                scopeId,
                AutopilotGlobalTaskInventoryScopeIds.ManusBridgeAuthenticatedUserWide,
                StringComparison.Ordinal)
            || cursor is not null)
            return null;

        var token = bridgeTokenProvider()?.Trim();
        if (string.IsNullOrWhiteSpace(token) || !IsValidBearerToken(token))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, tasksEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        var requestToken = deadline.Token;

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            if (response.StatusCode != System.Net.HttpStatusCode.OK) return null;
            var payload = await ManusTaskListInventoryPageSource.ReadBoundedAsync(
                response.Content,
                requestToken,
                MaximumAggregateResponseBytes);
            if (payload is null) return null;

            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || ContainsDuplicateJsonProperties(root)
                || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind != System.Text.Json.JsonValueKind.True
                || !root.TryGetProperty("manus_user_id", out var manusUserIdElement)
                || manusUserIdElement.ValueKind != System.Text.Json.JsonValueKind.String
                || !string.Equals(manusUserIdElement.GetString(), expectedManusUserId, StringComparison.Ordinal)
                || !root.TryGetProperty("data", out var tasks)
                || tasks.ValueKind != System.Text.Json.JsonValueKind.Array
                || tasks.GetArrayLength() > MaximumTasks
                || !root.TryGetProperty("has_more", out var hasMore)
                || hasMore.ValueKind != System.Text.Json.JsonValueKind.False
                || root.TryGetProperty("next_cursor", out _))
                return null;

            var items = new List<AutopilotGlobalTaskInventoryItem>(tasks.GetArrayLength());
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !task.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != System.Text.Json.JsonValueKind.String
                    || !task.TryGetProperty("status", out var statusElement)
                    || statusElement.ValueKind != System.Text.Json.JsonValueKind.String)
                    return null;

                var taskId = idElement.GetString()?.Trim() ?? string.Empty;
                if (taskId.Length != 22 || taskId.Any(character => !char.IsAsciiLetterOrDigit(character)) || !seenIds.Add(taskId))
                    return null;

                bool? hasRunningBackgroundJobs = null;
                if (task.TryGetProperty("has_running_background_jobs", out var backgroundElement))
                {
                    if (backgroundElement.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                        return null;
                    hasRunningBackgroundJobs = backgroundElement.GetBoolean();
                }

                var status = NormalizeBridgeTaskStatus(statusElement.GetString() ?? string.Empty, hasRunningBackgroundJobs);
                if (status is null) return null;

                // Creator metadata is deliberately ignored: all visible tasks are
                // counted; this response makes no Taslim-only ownership assertion.
                items.Add(new AutopilotGlobalTaskInventoryItem(
                    taskId,
                    AutopilotGlobalTaskInventoryScopeIds.ManusBridgeAuthenticatedUserWide,
                    status,
                    null));
            }

            return new AutopilotGlobalTaskInventoryPage(
                AutopilotGlobalTaskInventoryScopeIds.ManusBridgeAuthenticatedUserWide,
                items,
                null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Dispose() => http.Dispose();

    private static string? NormalizeBridgeTaskStatus(string status, bool? hasRunningBackgroundJobs)
    {
        var normalized = status.Trim().ToLowerInvariant();
        if (hasRunningBackgroundJobs == true) return "running";
        if (normalized.Length == 0 || (normalized == "error" && hasRunningBackgroundJobs is null)) return null;

        return normalized switch
        {
            "queued" or "pending" or "created" or "accepted" or "scheduled"
                or "running" or "in_progress" or "in-progress" or "processing" or "started" or "active"
                or "waiting" or "stopped" or "blocked" or "awaiting_input" or "awaiting_approval" or "needs_input" or "requires_human"
                or "completed" or "complete" or "succeeded" or "success" or "done" or "finished"
                or "failed" or "failure" or "error" or "errored" or "timed_out" or "timeout"
                or "cancelled" or "canceled" or "aborted" or "killed" or "terminated"
                => normalized,
            _ => null,
        };
    }

    private static bool IsValidBearerToken(string token)
    {
        var paddingStarted = false;
        var sawPayload = false;
        foreach (var character in token)
        {
            if (character == '=')
            {
                if (!sawPayload) return false;
                paddingStarted = true;
                continue;
            }

            if (paddingStarted
                || !(char.IsAsciiLetterOrDigit(character) || "-._~+/".IndexOf(character) >= 0))
                return false;
            sawPayload = true;
        }

        return sawPayload;
    }

    private static bool ContainsDuplicateJsonProperties(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name) || ContainsDuplicateJsonProperties(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsDuplicateJsonProperties(item)) return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A complete active-task snapshot for its declared inventory universe. Partial
/// pages are never represented as authoritative and therefore cannot grant capacity.
/// </summary>
public sealed record AutopilotGlobalTaskInventorySnapshot(
    string ScopeId,
    bool IsAuthoritative,
    int PagesRead,
    bool HasMorePages,
    string? FailureCode,
    IReadOnlyList<AutopilotGlobalTaskInventoryItem> ActiveTasks)
{
    public static AutopilotGlobalTaskInventorySnapshot Unknown(string scopeId, string failureCode, int pagesRead = 0) =>
        new(scopeId, false, pagesRead, true, failureCode, Array.Empty<AutopilotGlobalTaskInventoryItem>());
}

/// <summary>
/// Reads every page of the configured inventory universe. It rejects a failed,
/// repeated, truncated, mis-scoped, malformed, or status-ambiguous listing rather
/// than interpreting an unknown external occupancy as zero. Sources may represent
/// a narrower Taslim scope or a conservative authenticated-user-wide scope; they
/// must never relabel user-wide results as creator-filtered Taslim-only inventory.
/// </summary>
public sealed class AutopilotGlobalTaskInventoryReader(
    IAutopilotGlobalTaskInventoryPageSource source,
    string taslimScopeId,
    int maxPages = 256)
{
    public const int MaximumPages = 256;

    public async Task<AutopilotGlobalTaskInventorySnapshot> ReadAuthoritativeSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taslimScopeId))
            return AutopilotGlobalTaskInventorySnapshot.Unknown(string.Empty, "inventory_scope_missing");

        var pageLimit = Math.Clamp(maxPages, 1, MaximumPages);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, AutopilotGlobalTaskInventoryItem>(StringComparer.Ordinal);
        string? cursor = null;
        var pagesRead = 0;

        for (var pageNumber = 0; pageNumber < pageLimit; pageNumber++)
        {
            AutopilotGlobalTaskInventoryPage? page;
            try
            {
                page = await source.ReadPageAsync(taslimScopeId, cursor, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_source_unavailable", pagesRead);
            }

            pagesRead++;
            if (page is null || page.Items is null)
                return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_page_invalid", pagesRead);
            if (!string.Equals(page.QueryScopeId, taslimScopeId, StringComparison.Ordinal))
                return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_scope_mismatch", pagesRead);

            foreach (var item in page.Items)
            {
                if (string.IsNullOrWhiteSpace(item.TaskId) || string.IsNullOrWhiteSpace(item.ScopeId))
                    return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_task_identity_or_scope_missing", pagesRead);

                // Items from other declared inventory universes are deliberately ignored.
                // The Bridge user-wide source labels every visible task with its
                // dedicated user-wide scope, so unrelated creators remain counted.
                if (!string.Equals(item.ScopeId, taslimScopeId, StringComparison.Ordinal))
                    continue;

                var normalizedStatus = NormalizeStatus(item.Status);
                if (normalizedStatus == InventoryStatus.Unknown)
                    return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_task_status_unknown", pagesRead);
                if (normalizedStatus == InventoryStatus.Terminal)
                    continue;

                var taskId = item.TaskId.Trim();
                var idempotencyKey = string.IsNullOrWhiteSpace(item.IdempotencyKey) ? null : item.IdempotencyKey.Trim();
                if (idempotencyKey is { Length: > 200 })
                    return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_idempotency_key_invalid", pagesRead);

                var normalized = item with { TaskId = taskId, ScopeId = taslimScopeId, Status = item.Status.Trim().ToLowerInvariant(), IdempotencyKey = idempotencyKey };
                if (byId.TryGetValue(taskId, out var previous))
                {
                    if (!string.Equals(previous.ScopeId, normalized.ScopeId, StringComparison.Ordinal)
                        || !string.Equals(previous.Status, normalized.Status, StringComparison.Ordinal)
                        || !string.Equals(previous.IdempotencyKey, normalized.IdempotencyKey, StringComparison.Ordinal))
                        return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_duplicate_identity_conflict", pagesRead);
                    continue;
                }

                byId.Add(taskId, normalized);
            }

            if (page.NextCursor is null)
            {
                return new AutopilotGlobalTaskInventorySnapshot(
                    taslimScopeId,
                    true,
                    pagesRead,
                    false,
                    null,
                    byId.Values.OrderBy(item => item.TaskId, StringComparer.Ordinal).ToArray());
            }

            var nextCursor = page.NextCursor.Trim();
            if (nextCursor.Length == 0 || !seenCursors.Add(nextCursor) || string.Equals(cursor, nextCursor, StringComparison.Ordinal))
                return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_cursor_repeated_or_invalid", pagesRead);
            cursor = nextCursor;
        }

        return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_page_limit_exceeded", pagesRead);
    }

    private enum InventoryStatus { Active, Terminal, Unknown }

    private static InventoryStatus NormalizeStatus(string? value)
    {
        var status = (value ?? string.Empty).Trim().ToLowerInvariant();
        // Manus task_stopped may mean either finish or ask. task.list has no stop_reason,
        // so stopped tasks stay occupied until exact locally verified completion evidence
        // allows CountOccupiedSlotsAsync to reconcile that specific task ID.
        return status switch
        {
            "queued" or "pending" or "created" or "accepted" or "scheduled"
                or "running" or "in_progress" or "in-progress" or "processing" or "started" or "active"
                or "waiting" or "stopped" or "blocked" or "awaiting_input" or "awaiting_approval" or "needs_input" or "requires_human"
                => InventoryStatus.Active,
            "completed" or "complete" or "succeeded" or "success" or "done" or "finished"
                or "failed" or "failure" or "error" or "errored" or "timed_out" or "timeout"
                or "cancelled" or "canceled" or "aborted" or "killed" or "terminated"
                => InventoryStatus.Terminal,
            _ => InventoryStatus.Unknown,
        };
    }
}

public static class AutopilotGlobalTaskSlotOutcomes
{
    public const string Reserved = "reserved";
    public const string CapacityReached = "capacity_reached";
    public const string InventoryUnknown = "inventory_unknown";
    public const string AlreadyActive = "already_active";
    public const string Completed = "completed";
    public const string DuplicateCompletion = "duplicate_completion";
    public const string LaunchInProgress = "launch_in_progress";
    public const string AmbiguousLaunch = "ambiguous_launch";
    public const string LaunchRejected = "launch_rejected";
    public const string AttemptsExhausted = "attempts_exhausted";
    public const string InvalidTransition = "invalid_transition";
    public const string TerminalEvidenceInvalid = "terminal_evidence_invalid";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string ReservationUnavailable = "reservation_unavailable";
}

/// <summary>Result of an atomic slot or lifecycle transition.</summary>
public sealed record AutopilotGlobalTaskSlotResult(
    string Outcome,
    bool SlotHeld,
    bool MayCallProvider,
    int ActiveSlotsObserved,
    string? ExternalTaskId = null,
    string? ReasonCode = null);

/// <summary>
/// Atomically claims and maintains the single global Taslim task cap across all
/// waves and API processes. It uses the already-durable AutopilotWaveLaunchTask
/// row as the reservation: Launching and Launched hold one slot; Completed and a
/// deterministic provider rejection release it. PostgreSQL transaction advisory
/// locking serializes independent processes; serializable transactions are also
/// used for relational providers and SQLite tests.
///
/// This component must be placed around every Taslim Manus launch path to enforce
/// the cap. Until the launch service and completion/reconciliation call sites are
/// wired through it, it is a tested component, not an effective production limit.
/// </summary>
public sealed class AutopilotGlobalTaskSlotAllocator
{
    public const int HardMaximum = 20;
    private const string SlotReservedReason = "global_slot_reserved";
    private const int AdvisoryLockNamespace = 1_413_560_652; // ASCII "TASL"
    private const int AdvisoryLockResource = 20;
    private const int MaxRowsExamined = 10_000;
    private const int MaxRelationalRetries = 5;

    // Protects a single scoped allocator/DbContext. Independent instances are
    // serialized by the relational transaction and PostgreSQL advisory lock.
    private readonly SemaphoreSlim LocalReservationGate = new(1, 1);

    private readonly TaslimDbContext db;
    private readonly TimeProvider clock;
    private readonly string scopeId;
    private readonly int capacity;

    public AutopilotGlobalTaskSlotAllocator(
        TaslimDbContext db,
        string taslimScopeId,
        TimeProvider? timeProvider = null,
        int maxConcurrentTasks = HardMaximum)
    {
        this.db = db;
        scopeId = string.IsNullOrWhiteSpace(taslimScopeId)
            ? throw new ArgumentException("A stable Taslim inventory scope is required.", nameof(taslimScopeId))
            : taslimScopeId.Trim();
        if (maxConcurrentTasks < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentTasks));
        capacity = Math.Min(maxConcurrentTasks, HardMaximum);
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reserves a slot before the external provider call. Stable idempotency keys
    /// map to the existing row's wave/task identity. An expired ambiguous launch
    /// is retried only when the caller confirms the bridge enforces that key.
    /// </summary>
    public async Task<AutopilotGlobalTaskSlotResult> ReserveOrRecoverLaunchAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        AutopilotGlobalTaskInventorySnapshot inventory,
        TimeSpan launchLease,
        bool providerGuaranteesIdempotency,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthoritative(inventory))
            return new(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, false, false, 0, ReasonCode: inventory?.FailureCode ?? "inventory_unknown");

        if (launchLease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(launchLease));
        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            if (!db.Database.IsRelational())
                return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, false, false, 0, ReasonCode: "relational_store_required");

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(TransactionIsolationLevel, cancellationToken);
                    await AcquireCrossProcessFenceAsync(cancellationToken);

                    var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
                    if (task is null)
                        return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, false, false, 0, ReasonCode: "launch_task_missing");

                    var expectedKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
                    if (!string.Equals(expectedKey, idempotencyKey, StringComparison.Ordinal))
                        return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");

                    var legacyKey = AutopilotNextWavePolicy.LaunchIdempotencyKey(task.WaveKey, task.TaskKey);
                    if (!string.Equals(legacyKey, expectedKey, StringComparison.Ordinal)
                        && inventory.ActiveTasks.Any(item => string.Equals(item.IdempotencyKey, legacyKey, StringComparison.Ordinal)
                            && (string.IsNullOrWhiteSpace(task.ExternalTaskId)
                                || !string.Equals(item.TaskId, task.ExternalTaskId, StringComparison.Ordinal))))
                        return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, inventory.ActiveTasks.Count,
                            ReasonCode: "legacy_idempotency_identity_ambiguous_requires_reconciliation");

                    var duplicateDatabaseIdentity = await db.AutopilotWaveLaunchTasks.AsNoTracking()
                        .AnyAsync(item => item.Id != task.Id && item.WaveKey == task.WaveKey && item.TaskKey == task.TaskKey, cancellationToken);
                    if (duplicateDatabaseIdentity)
                        return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "duplicate_wave_task_identity");

                    var inventoryMatches = inventory.ActiveTasks
                        .Where(item => string.Equals(item.IdempotencyKey, expectedKey, StringComparison.Ordinal)
                            || (!string.IsNullOrWhiteSpace(task.ExternalTaskId)
                                && string.Equals(item.TaskId, task.ExternalTaskId, StringComparison.Ordinal)))
                        .ToList();
                    if (inventoryMatches.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() > 1)
                        return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, true, false, inventory.ActiveTasks.Count, ReasonCode: "multiple_external_tasks_for_idempotency_key");

                    if (string.Equals(task.Status, AutopilotWaveLaunchStatuses.Completed, StringComparison.OrdinalIgnoreCase))
                        return new(AutopilotGlobalTaskSlotOutcomes.Completed, false, false, 0, task.ExternalTaskId);

                    if (inventoryMatches.Count == 1)
                    {
                        if (!string.IsNullOrWhiteSpace(task.ExternalTaskId)
                            && !string.Equals(task.ExternalTaskId, inventoryMatches[0].TaskId, StringComparison.Ordinal))
                            return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, true, false, inventory.ActiveTasks.Count,
                                task.ExternalTaskId, "provider_idempotency_key_returned_different_task_id");

                        task.ExternalTaskId = inventoryMatches[0].TaskId;
                        task.Status = AutopilotWaveLaunchStatuses.Launched;
                        task.ReasonCode = "recovered_from_authoritative_inventory";
                        task.LastStatusDetail = "existing_taslim_task_reused; no_duplicate_launch";
                        task.LaunchedAt ??= UtcNow();
                        task.UpdatedAt = UtcNow();
                        task.ConcurrencyToken = Guid.NewGuid();
                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return new(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, true, false, inventory.ActiveTasks.Count, task.ExternalTaskId);
                    }

                    if (string.Equals(task.Status, AutopilotWaveLaunchStatuses.Launched, StringComparison.OrdinalIgnoreCase))
                        return new(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, true, false, inventory.ActiveTasks.Count, task.ExternalTaskId);

                    // The current controller claims a durable row immediately before it
                    // calls IWaveLaunchProvider. Treat that fresh, unmarked claim as a
                    // candidate, then atomically convert it into the global slot lease.
                    // All other Launching rows, marked or not, remain conservatively occupied.
                    if (task.Status == AutopilotWaveLaunchStatuses.Launching && !IsGlobalSlotReservation(task))
                    {
                        var staleUnreservedClaim = UtcNow() - task.UpdatedAt >= launchLease;
                        var occupiedBeforeClaim = await CountOccupiedSlotsAsync(inventory, cancellationToken, task.Id);
                        if (occupiedBeforeClaim >= capacity)
                            return new(AutopilotGlobalTaskSlotOutcomes.CapacityReached, false, false, occupiedBeforeClaim, ReasonCode: "global_taslim_slot_cap_reached");

                        if (staleUnreservedClaim)
                        {
                            // A stale unmarked claim may predate this allocator and use
                            // the legacy truncated key. Retain the slot, but do not retry
                            // under a new key until authoritative reconciliation binds it.
                            task.ReasonCode = SlotReservedReason;
                            task.LastStatusDetail = "stale_preallocator_claim_held_for_reconciliation";
                            task.UpdatedAt = UtcNow();
                            task.ConcurrencyToken = Guid.NewGuid();
                            await db.SaveChangesAsync(cancellationToken);
                            await transaction.CommitAsync(cancellationToken);
                            return new(task.LaunchAttempt >= task.MaxLaunchAttempts
                                    ? AutopilotGlobalTaskSlotOutcomes.AttemptsExhausted
                                    : AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch,
                                true, false, occupiedBeforeClaim + 1, task.ExternalTaskId, "stale_unmarked_claim_requires_reconciliation");
                        }

                        task.ReasonCode = SlotReservedReason;
                        task.LastStatusDetail = $"global_slot_claimed:{occupiedBeforeClaim + 1}/{capacity}";
                        task.ConcurrencyToken = Guid.NewGuid();
                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return new(AutopilotGlobalTaskSlotOutcomes.Reserved, true, true, occupiedBeforeClaim + 1,
                            ReasonCode: null);
                    }

                    if (task.Status == AutopilotWaveLaunchStatuses.Launching && IsGlobalSlotReservation(task))
                    {
                        if (UtcNow() - task.UpdatedAt < launchLease)
                            return new(AutopilotGlobalTaskSlotOutcomes.LaunchInProgress, true, false, inventory.ActiveTasks.Count, task.ExternalTaskId);

                        if (!providerGuaranteesIdempotency)
                            return new(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, true, false, inventory.ActiveTasks.Count, ReasonCode: "expired_claim_but_provider_idempotency_unverified");

                        if (task.LaunchAttempt >= task.MaxLaunchAttempts)
                            return new(AutopilotGlobalTaskSlotOutcomes.AttemptsExhausted, true, false, inventory.ActiveTasks.Count, ReasonCode: "ambiguous_retry_bound_exhausted");

                        var observed = await CountOccupiedSlotsAsync(inventory, cancellationToken);
                        if (observed > capacity)
                            return new(AutopilotGlobalTaskSlotOutcomes.CapacityReached, true, false, observed, task.ExternalTaskId, "global_taslim_slot_cap_already_exceeded");
                        task.LaunchAttempt++;
                        task.UpdatedAt = UtcNow();
                        task.ReasonCode = SlotReservedReason;
                        task.LastStatusDetail = "slot_retained; provider_retry_requires_idempotency";
                        task.ConcurrencyToken = Guid.NewGuid();
                        await db.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return new(AutopilotGlobalTaskSlotOutcomes.Reserved, true, true, observed, ReasonCode: "stale_launch_retried_idempotently");
                    }

                    if (task.Status != AutopilotWaveLaunchStatuses.Planned && task.Status != AutopilotWaveLaunchStatuses.Failed)
                        return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, inventory.ActiveTasks.Count, task.ExternalTaskId, "task_not_launchable");

                    if (task.LaunchAttempt >= task.MaxLaunchAttempts)
                        return new(AutopilotGlobalTaskSlotOutcomes.AttemptsExhausted, false, false, inventory.ActiveTasks.Count, ReasonCode: "launch_attempt_bound_exhausted");

                    var occupied = await CountOccupiedSlotsAsync(inventory, cancellationToken);
                    if (occupied >= capacity)
                        return new(AutopilotGlobalTaskSlotOutcomes.CapacityReached, false, false, occupied, ReasonCode: "global_taslim_slot_cap_reached");

                    task.Status = AutopilotWaveLaunchStatuses.Launching;
                    task.LaunchAttempt++;
                    task.UpdatedAt = UtcNow();
                    task.CompletedAt = null;
                    task.ReasonCode = SlotReservedReason;
                    task.LastStatusDetail = $"global_slot_claimed:{occupied + 1}/{capacity}";
                    task.ConcurrencyToken = Guid.NewGuid();
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(AutopilotGlobalTaskSlotOutcomes.Reserved, true, true, occupied + 1);
                }
                catch (Exception exception) when (IsRetryableRelationalConflict(exception))
                {
                    db.ChangeTracker.Clear();
                    if (attempt >= MaxRelationalRetries)
                        return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, false, false, 0,
                            ReasonCode: "relational_transaction_conflict_retries_exhausted");

                    await Task.Delay(RetryDelay(attempt), cancellationToken);
                }
            }
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    /// <summary>Persists provider acceptance without creating a second reservation.</summary>
    public async Task<AutopilotGlobalTaskSlotResult> RecordAcceptedAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        string externalTaskId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalTaskId) || externalTaskId.Trim().Length > 200)
            return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, false, false, 0, ReasonCode: "external_task_id_invalid");

        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(TransactionIsolationLevel, cancellationToken);
                    await AcquireCrossProcessFenceAsync(cancellationToken);
                    var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
                    if (task is null || !MatchesKey(task, idempotencyKey))
                        return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");

                    if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                        return new(AutopilotGlobalTaskSlotOutcomes.Completed, false, false, 0, task.ExternalTaskId);
                    if (task.Status == AutopilotWaveLaunchStatuses.Launched)
                    {
                        var sameTask = string.Equals(task.ExternalTaskId, externalTaskId.Trim(), StringComparison.Ordinal);
                        return new(sameTask ? AutopilotGlobalTaskSlotOutcomes.AlreadyActive : AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict,
                            true, false, 0, task.ExternalTaskId, sameTask ? null : "provider_returned_conflicting_task_id");
                    }
                    if (task.Status != AutopilotWaveLaunchStatuses.Launching)
                        return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, 0, task.ExternalTaskId, "launch_claim_not_held");

                    task.ExternalTaskId = externalTaskId.Trim();
                    task.Status = AutopilotWaveLaunchStatuses.Launched;
                    task.LaunchedAt ??= UtcNow();
                    task.UpdatedAt = UtcNow();
                    task.ReasonCode = "launched";
                    task.LastStatusDetail = "external_task_identity_persisted";
                    task.ConcurrencyToken = Guid.NewGuid();
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, true, false, 0, task.ExternalTaskId);
                }
                catch (Exception exception) when (IsRetryableRelationalConflict(exception))
                {
                    db.ChangeTracker.Clear();
                    if (attempt >= MaxRelationalRetries)
                        return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, true, false, 0,
                            externalTaskId.Trim(), "relational_transaction_conflict_retries_exhausted");

                    await Task.Delay(RetryDelay(attempt), cancellationToken);
                }
            }
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    /// <summary>
    /// A deterministic provider rejection proves no external task was accepted,
    /// so the durable reservation can be released. Network timeouts are not
    /// deterministic and must use RecordAmbiguousAsync instead.
    /// </summary>
    public async Task<AutopilotGlobalTaskSlotResult> RecordRejectedAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(TransactionIsolationLevel, cancellationToken);
                    await AcquireCrossProcessFenceAsync(cancellationToken);
                    var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
                    if (task is null || !MatchesKey(task, idempotencyKey))
                        return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");
                    if (task.Status == AutopilotWaveLaunchStatuses.Failed)
                        return new(AutopilotGlobalTaskSlotOutcomes.LaunchRejected, false, false, 0, task.ExternalTaskId, task.ReasonCode);
                    if (task.Status != AutopilotWaveLaunchStatuses.Launching)
                        return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, task.Status == AutopilotWaveLaunchStatuses.Launched, false, 0, task.ExternalTaskId, "launch_claim_not_held");

                    task.Status = AutopilotWaveLaunchStatuses.Failed;
                    task.ReasonCode = SafeCode(reasonCode, "provider_rejected");
                    task.LastStatusDetail = "provider_rejected_before_task_acceptance";
                    task.CompletedAt = UtcNow();
                    task.UpdatedAt = UtcNow();
                    task.ConcurrencyToken = Guid.NewGuid();
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(AutopilotGlobalTaskSlotOutcomes.LaunchRejected, false, false, 0, task.ExternalTaskId, task.ReasonCode);
                }
                catch (Exception exception) when (IsRetryableRelationalConflict(exception))
                {
                    db.ChangeTracker.Clear();
                    if (attempt >= MaxRelationalRetries)
                        return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, true, false, 0,
                            ReasonCode: "relational_transaction_conflict_retries_exhausted");

                    await Task.Delay(RetryDelay(attempt), cancellationToken);
                }
            }
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    /// <summary>Retains and refreshes the slot after an ambiguous provider timeout.</summary>
    public async Task<AutopilotGlobalTaskSlotResult> RecordAmbiguousAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        string reasonCode = "provider_outcome_unknown",
        CancellationToken cancellationToken = default)
    {
        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(TransactionIsolationLevel, cancellationToken);
                    await AcquireCrossProcessFenceAsync(cancellationToken);
                    var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
                    if (task is null || !MatchesKey(task, idempotencyKey))
                        return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");
                    if (task.Status == AutopilotWaveLaunchStatuses.Launched)
                        return new(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, true, false, 0, task.ExternalTaskId);
                    if (task.Status != AutopilotWaveLaunchStatuses.Launching)
                        return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, 0, task.ExternalTaskId, "launch_claim_not_held");

                    task.ReasonCode = SlotReservedReason;
                    task.LastStatusDetail = $"slot_retained_until_authoritative_reconciliation:{SafeCode(reasonCode, "provider_outcome_unknown")}";
                    task.UpdatedAt = UtcNow();
                    task.ConcurrencyToken = Guid.NewGuid();
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, true, false, 0, task.ExternalTaskId, task.ReasonCode);
                }
                catch (Exception exception) when (IsRetryableRelationalConflict(exception))
                {
                    db.ChangeTracker.Clear();
                    if (attempt >= MaxRelationalRetries)
                        return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, true, false, 0,
                            ReasonCode: "relational_transaction_conflict_retries_exhausted");

                    await Task.Delay(RetryDelay(attempt), cancellationToken);
                }
            }
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    /// <summary>
    /// Releases exactly one durable slot only when the persisted event is processed,
    /// signature-verified, replay-protected, and bound to this wave/task plus its
    /// stored external task ID. Replays with the same evidence are harmless.
    /// </summary>
    public async Task<AutopilotGlobalTaskSlotResult> CompleteVerifiedAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        string externalTaskId,
        Guid terminalEventId,
        CancellationToken cancellationToken = default)
    {
        if (terminalEventId == Guid.Empty)
            return new(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid, false, false, 0, ReasonCode: "terminal_event_id_required");
        if (string.IsNullOrWhiteSpace(externalTaskId) || externalTaskId.Trim().Length > 200)
            return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, 0, ReasonCode: "external_task_id_invalid");

        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(TransactionIsolationLevel, cancellationToken);
                    await AcquireCrossProcessFenceAsync(cancellationToken);
                    var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
                    if (task is null || !MatchesKey(task, idempotencyKey))
                        return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");

                    var externalId = externalTaskId.Trim();
                    if (string.IsNullOrWhiteSpace(task.ExternalTaskId)
                        || !string.Equals(task.ExternalTaskId, externalId, StringComparison.Ordinal))
                        return new(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid,
                            task.Status is AutopilotWaveLaunchStatuses.Launching or AutopilotWaveLaunchStatuses.Launched,
                            false, 0, task.ExternalTaskId, "external_task_identity_not_bound_to_launch_row");

                    var terminalEvent = await db.AutopilotEvents.AsNoTracking()
                        .SingleOrDefaultAsync(item => item.Id == terminalEventId, cancellationToken);
                    if (!HasBoundVerifiedCompletionEvidence(terminalEvent, task, externalId))
                        return new(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid,
                            task.Status is AutopilotWaveLaunchStatuses.Launching or AutopilotWaveLaunchStatuses.Launched,
                            false, 0, task.ExternalTaskId, "terminal_event_not_verified_for_launch_task");

                    if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                        return new(AutopilotGlobalTaskSlotOutcomes.DuplicateCompletion, false, false, 0, task.ExternalTaskId);
                    if (task.Status != AutopilotWaveLaunchStatuses.Launching && task.Status != AutopilotWaveLaunchStatuses.Launched)
                        return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, 0, task.ExternalTaskId, "task_has_no_reserved_slot");

                    task.ExternalTaskId = externalId;
                    task.Status = AutopilotWaveLaunchStatuses.Completed;
                    task.CompletedAt = UtcNow();
                    task.UpdatedAt = UtcNow();
                    task.ReasonCode = "verified_terminal_evidence";
                    task.LastStatusDetail = "slot_released_once";
                    task.ConcurrencyToken = Guid.NewGuid();
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(AutopilotGlobalTaskSlotOutcomes.Completed, false, false, 0, task.ExternalTaskId);
                }
                catch (Exception exception) when (IsRetryableRelationalConflict(exception))
                {
                    db.ChangeTracker.Clear();
                    if (attempt >= MaxRelationalRetries)
                        return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, true, false, 0,
                            externalTaskId.Trim(), "relational_transaction_conflict_retries_exhausted");

                    await Task.Delay(RetryDelay(attempt), cancellationToken);
                }
            }
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    private async Task<int> CountOccupiedSlotsAsync(
        AutopilotGlobalTaskInventorySnapshot inventory,
        CancellationToken cancellationToken,
        Guid? excludeUnreservedLaunchTaskRowId = null)
    {
        var externalIds = inventory.ActiveTasks.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).ToArray();
        var recentlyCompletedIds = externalIds.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.AutopilotWaveLaunchTasks.AsNoTracking()
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Completed
                    && item.ExternalTaskId != null
                    && externalIds.Contains(item.ExternalTaskId))
                .Select(item => item.ExternalTaskId!)
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        // A local verified completion only deduplicates an external ID while Manus
        // still reports it stopped; a resumed running/waiting task occupies capacity again.
        var externalActive = inventory.ActiveTasks
            .Where(item => !recentlyCompletedIds.Contains(item.TaskId)
                || !string.Equals(item.Status, "stopped", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var externalActiveIds = externalActive.Select(item => item.TaskId).ToHashSet(StringComparer.Ordinal);
        var externalActiveKeys = externalActive
            .Where(item => !string.IsNullOrWhiteSpace(item.IdempotencyKey))
            .Select(item => item.IdempotencyKey!)
            .ToHashSet(StringComparer.Ordinal);

        var localActiveQuery = db.AutopilotWaveLaunchTasks.AsNoTracking()
            .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched
                || item.Status == AutopilotWaveLaunchStatuses.Launching);
        if (excludeUnreservedLaunchTaskRowId.HasValue)
        {
            var candidateId = excludeUnreservedLaunchTaskRowId.Value;
            localActiveQuery = localActiveQuery.Where(item => item.Id != candidateId || item.Status == AutopilotWaveLaunchStatuses.Launched);
        }

        var localActive = await localActiveQuery
            .Take(MaxRowsExamined + 1)
            .ToListAsync(cancellationToken);
        if (localActive.Count > MaxRowsExamined)
            return HardMaximum;

        var occupied = new HashSet<string>(externalActiveIds.Select(id => $"external:{id}"), StringComparer.Ordinal);
        foreach (var reservation in localActive)
        {
            if (!string.IsNullOrWhiteSpace(reservation.ExternalTaskId)
                && externalActiveIds.Contains(reservation.ExternalTaskId))
                continue;

            var key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(reservation.WaveKey, reservation.TaskKey);
            if (string.IsNullOrWhiteSpace(reservation.ExternalTaskId) && externalActiveKeys.Contains(key))
                continue;

            occupied.Add(string.IsNullOrWhiteSpace(reservation.ExternalTaskId)
                ? $"reservation:{reservation.Id:D}"
                : $"database-task:{reservation.ExternalTaskId}");
        }

        return occupied.Count;
    }

    private async Task AcquireCrossProcessFenceAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return;

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT pg_advisory_xact_lock({AdvisoryLockNamespace}, {AdvisoryLockResource})";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private IsolationLevel TransactionIsolationLevel =>
        string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal)
            ? IsolationLevel.ReadCommitted
            : IsolationLevel.Serializable;

    private static bool IsRetryableRelationalConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException)
                return true;
            if (current is Npgsql.PostgresException postgres
                && (postgres.SqlState == "40001" || postgres.SqlState == "40P01"))
                return true;
        }

        return false;
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(10 * (1 << Math.Min(attempt, 4)));

    private bool IsAuthoritative(AutopilotGlobalTaskInventorySnapshot? inventory)
    {
        if (inventory is null
            || !inventory.IsAuthoritative
            || inventory.PagesRead < 1
            || inventory.HasMorePages
            || !string.IsNullOrWhiteSpace(inventory.FailureCode)
            || !string.Equals(inventory.ScopeId, scopeId, StringComparison.Ordinal)
            || inventory.ActiveTasks is null)
            return false;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in inventory.ActiveTasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskId)
                || !string.Equals(task.ScopeId, scopeId, StringComparison.Ordinal)
                || !IsKnownActiveStatus(task.Status)
                || !ids.Add(task.TaskId))
                return false;
            if (task.IdempotencyKey is { Length: > 200 }) return false;
        }
        return true;
    }

    private bool MatchesKey(AutopilotWaveLaunchTask task, string idempotencyKey) =>
        string.Equals(AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey), idempotencyKey, StringComparison.Ordinal);

    private static bool HasBoundVerifiedCompletionEvidence(
        AutopilotEvent? evidence,
        AutopilotWaveLaunchTask task,
        string externalTaskId)
    {
        if (evidence is null
            || !string.Equals(evidence.EventType, AutopilotEventTypes.WaveTaskCompleted, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.Status, AutopilotEventStatuses.Processed, StringComparison.OrdinalIgnoreCase)
            || !evidence.SignatureVerified
            || !evidence.ReplayProtected
            || evidence.ProcessedAt is null
            || string.IsNullOrWhiteSpace(evidence.SourceSystem)
            || string.IsNullOrWhiteSpace(evidence.ExternalEventId)
            || string.IsNullOrWhiteSpace(evidence.IdempotencyKey)
            || string.IsNullOrWhiteSpace(evidence.PayloadHash)
            || !string.Equals(evidence.WaveKey, task.WaveKey, StringComparison.Ordinal)
            || !string.Equals(evidence.TaskId, task.TaskKey, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(evidence.SignalJson))
            return false;

        try
        {
            var signal = System.Text.Json.JsonSerializer.Deserialize<AutopilotCompletionSignal>(evidence.SignalJson);
            if (signal is null
                || !string.Equals(signal.WaveKey, task.WaveKey, StringComparison.Ordinal)
                || !string.Equals(signal.TaskId, task.TaskKey, StringComparison.Ordinal)
                || !AutopilotInputValidation.TryNormalizeOutcome(signal.Outcome, out var normalizedOutcome)
                || string.Equals(normalizedOutcome, AutopilotTaskOutcomes.Blocked, StringComparison.OrdinalIgnoreCase))
                return false;

            return HasBoundTaskDetailTerminalReceipt(signal.Evidence, externalTaskId, evidence.ExternalEventId);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool HasBoundTaskDetailTerminalReceipt(string? evidenceJson, string externalTaskId, string externalEventId)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson) || evidenceJson.Length > 4096)
            return false;

        try
        {
            var receipt = System.Text.Json.JsonSerializer.Deserialize<AutopilotGlobalTaskDetailTerminalReceipt>(evidenceJson);
            return receipt is not null
                && string.Equals(receipt.Kind, AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind, StringComparison.Ordinal)
                && string.Equals(receipt.TaskId, externalTaskId, StringComparison.Ordinal)
                && string.Equals(receipt.EventId, externalEventId, StringComparison.Ordinal)
                && string.Equals(receipt.AgentStatus, "stopped", StringComparison.OrdinalIgnoreCase)
                && string.Equals(receipt.StopReason, "finish", StringComparison.OrdinalIgnoreCase)
                && receipt.HasRunningBackgroundJobs == false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool IsGlobalSlotReservation(AutopilotWaveLaunchTask task) =>
        task.Status == AutopilotWaveLaunchStatuses.Launched
        || (task.Status == AutopilotWaveLaunchStatuses.Launching && task.ReasonCode == SlotReservedReason);

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;

    private static bool IsKnownActiveStatus(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "queued" or "pending" or "created" or "accepted" or "scheduled" or "running"
            or "in_progress" or "in-progress" or "processing" or "started" or "active"
            or "waiting" or "stopped" or "blocked" or "awaiting_input" or "awaiting_approval" or "needs_input" or "requires_human" => true,
        _ => false,
    };

    private static string SafeCode(string? value, string fallback)
    {
        var normalized = new string((value ?? string.Empty).Trim().ToLowerInvariant()
            .Take(120).Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        return normalized.Length == 0 ? fallback : normalized;
    }
}
