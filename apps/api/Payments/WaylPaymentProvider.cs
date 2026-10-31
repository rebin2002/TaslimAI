using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;

namespace Taslim.Api.Payments;

/// <summary>
/// Supplies a server-owned IQD checkout amount. Implementations must derive the amount from
/// trusted pricing/configuration, never from a browser request. No implementation or wiring is
/// included in this test-only integration.
/// </summary>
public interface IWaylIqdPriceCalculator
{
    ValueTask<int> CalculateTotalIqdAsync(string planCode, CancellationToken cancellationToken = default);
}

public sealed record WaylCheckoutCommand(
    string ReferenceId,
    string PlanCode,
    string DisplayLabel);

public enum WaylPaymentState
{
    Unknown,
    Created,
    Pending,
    Processing,
    Complete,
    Delivered,
    Cancelled,
    Rejected,
    Returned,
    RefundRequested,
    Refunded,
    RefundRejected,
    RefundCancelled,
    ChargebackOpen,
    ChargebackWon,
    ChargebackLost,
    ChargebackReversed,
}

public enum WaylPaymentVerificationOutcome
{
    NotFound,
    AmountOrCurrencyMismatch,
    NotCompleted,
    ProviderCompleted,
}

public enum WaylReplayDisposition
{
    FirstSeen,
    Duplicate,
    EventIdConflict,
}

public sealed record WaylCheckoutSession(
    string ReferenceId,
    int TotalIqd,
    Uri? HostedCheckoutUrl,
    WaylPaymentState State,
    string? WaylLinkId,
    string? Code,
    bool ReusedExistingReference);

public sealed record WaylPaymentVerification(
    string ReferenceId,
    WaylPaymentVerificationOutcome Outcome,
    WaylPaymentState ProviderState,
    int? TotalIqd,
    string? Currency,
    DateTimeOffset? CompletedAt,
    bool ApiLookupSucceeded,
    string? Reason)
{
    public bool ProviderReportsCompletedPayment => Outcome == WaylPaymentVerificationOutcome.ProviderCompleted;

    // Customer credit/subscription provisioning is deliberately not available in this adapter.
    // It remains blocked until issue #330, accounting review, and the Free-tier provider-cost gate
    // are independently resolved and the application payment lifecycle is owner-approved.
    public bool CanGrantCredits => false;
}

public sealed record WaylWebhookReceipt(
    string EventId,
    string ReferenceId,
    string EventName,
    string? PaymentStatus,
    int? TotalIqd,
    WaylPaymentState State,
    string PayloadSha256,
    WaylReplayDisposition ReplayDisposition)
{
    public bool SignatureVerified => true;
    public bool RequiresAuthenticatedApiVerification => State is WaylPaymentState.Complete or WaylPaymentState.Delivered;
}

public interface IWaylWebhookReplayGuard
{
    WaylReplayDisposition Register(string eventId, string payloadSha256);
}

/// <summary>
/// Volatile, process-local replay guard for local tests only. Production must use the existing
/// durable payment-event uniqueness boundary; this class is intentionally not registered.
/// </summary>
public sealed class InMemoryWaylWebhookReplayGuard : IWaylWebhookReplayGuard
{
    private readonly ConcurrentDictionary<string, string> eventHashes = new(StringComparer.Ordinal);

    public WaylReplayDisposition Register(string eventId, string payloadSha256)
    {
        if (eventHashes.TryAdd(eventId, payloadSha256))
            return WaylReplayDisposition.FirstSeen;

        return eventHashes.TryGetValue(eventId, out var priorHash)
            && string.Equals(priorHash, payloadSha256, StringComparison.OrdinalIgnoreCase)
                ? WaylReplayDisposition.Duplicate
                : WaylReplayDisposition.EventIdConflict;
    }
}

public static class WaylPaymentStateMapper
{
    /// <summary>
    /// Maps documented order/refund statuses and conservatively recognizes dispute strings if
    /// supplied. Wayl's public integration guide does not document chargeback event names; these
    /// dispute states are defensive models, not a claim that Wayl emits those events.
    /// </summary>
    public static WaylPaymentState Map(string? eventName, string? paymentStatus, string? refundStatus = null)
    {
        var eventText = eventName?.Trim() ?? string.Empty;
        var statusText = paymentStatus?.Trim() ?? string.Empty;
        var refundText = refundStatus?.Trim() ?? string.Empty;
        var combined = string.Join(' ', eventText, statusText, refundText).ToLowerInvariant();

        if (combined.Contains("chargeback", StringComparison.Ordinal) || combined.Contains("dispute", StringComparison.Ordinal))
        {
            if (combined.Contains("reversed", StringComparison.Ordinal)) return WaylPaymentState.ChargebackReversed;
            if (combined.Contains("won", StringComparison.Ordinal)) return WaylPaymentState.ChargebackWon;
            if (combined.Contains("lost", StringComparison.Ordinal)) return WaylPaymentState.ChargebackLost;
            return WaylPaymentState.ChargebackOpen;
        }

        var refundValue = refundText;
        if (refundValue.Length == 0 && eventText.Contains("refund", StringComparison.OrdinalIgnoreCase))
        {
            refundValue = statusText;
            if (refundValue.Length == 0)
                refundValue = eventText.Split(['.', '_', ' '], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        }
        if (refundValue.Length > 0)
        {
            switch (refundValue.ToLowerInvariant())
            {
                case "requested": return WaylPaymentState.RefundRequested;
                case "refunded": return WaylPaymentState.Refunded;
                case "rejected": return WaylPaymentState.RefundRejected;
                case "cancelled":
                case "canceled": return WaylPaymentState.RefundCancelled;
            }
        }

        return statusText.ToLowerInvariant() switch
        {
            "created" => WaylPaymentState.Created,
            "pending" => WaylPaymentState.Pending,
            "processing" => WaylPaymentState.Processing,
            "complete" or "completed" => WaylPaymentState.Complete,
            "delivered" => WaylPaymentState.Delivered,
            "cancelled" or "canceled" => WaylPaymentState.Cancelled,
            "rejected" => WaylPaymentState.Rejected,
            "returned" => WaylPaymentState.Returned,
            _ => WaylPaymentState.Unknown,
        };
    }
}

/// <summary>
/// Isolated Wayl API adapter. It always sends env=test, is not registered in Taslim DI, and has
/// no code path for enabling live mode or granting credits. All HTTP in tests is intercepted by a
/// local fake HttpMessageHandler. Do not use as production payment wiring.
/// </summary>
public sealed class WaylPaymentProvider
{
    public const string ApiBaseUrl = "https://api.thewayl.com/";
    public const string TestEnvironment = "test";
    public const string ApiAuthenticationHeader = "X-WAYL-AUTHENTICATION";
    public const string WebhookSignatureHeader = "x-wayl-signature-256";
    private const int MaximumWebhookBytes = 262_144;
    private const int MinimumWebhookSecretLength = 10;

    private readonly HttpClient httpClient;
    private readonly IWaylIqdPriceCalculator priceCalculator;
    private readonly string apiToken;
    private readonly string? webhookUrl;
    private readonly string? webhookSecret;
    private readonly string? redirectionUrl;
    private readonly IWaylWebhookReplayGuard replayGuard;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> referenceGates = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public WaylPaymentProvider(
        HttpClient httpClient,
        IWaylIqdPriceCalculator priceCalculator,
        string apiToken,
        string? webhookUrl = null,
        string? webhookSecret = null,
        string? redirectionUrl = null,
        IWaylWebhookReplayGuard? replayGuard = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.priceCalculator = priceCalculator ?? throw new ArgumentNullException(nameof(priceCalculator));
        if (string.IsNullOrWhiteSpace(apiToken) || apiToken.Any(char.IsControl))
            throw new ArgumentException("A server-side Wayl API token is required.", nameof(apiToken));
        this.apiToken = apiToken.Trim();

        if ((webhookUrl is null) != (webhookSecret is null))
            throw new ArgumentException("A per-link webhook URL and signing secret must be configured together.");
        if (webhookUrl is not null)
        {
            ValidateHttpsUrl(webhookUrl, nameof(webhookUrl));
            if (string.IsNullOrWhiteSpace(webhookSecret) || webhookSecret.Length < MinimumWebhookSecretLength)
                throw new ArgumentException("The Wayl webhook secret must be at least 10 characters.", nameof(webhookSecret));
        }
        if (redirectionUrl is not null)
            ValidateHttpsUrl(redirectionUrl, nameof(redirectionUrl));

        this.webhookUrl = webhookUrl;
        this.webhookSecret = webhookSecret;
        this.redirectionUrl = redirectionUrl;
        this.replayGuard = replayGuard ?? new InMemoryWaylWebhookReplayGuard();
    }

    /// <summary>
    /// Reads only server-process environment variables. It never writes or provisions secrets.
    /// Use an approved secret manager to supply WAYL_API_TOKEN and, when a webhook is configured,
    /// WAYL_WEBHOOK_SECRET. The caller still has to keep this adapter disconnected from production.
    /// </summary>
    public static WaylPaymentProvider FromEnvironment(
        HttpClient httpClient,
        IWaylIqdPriceCalculator priceCalculator,
        string? webhookUrl = null,
        string? redirectionUrl = null,
        IWaylWebhookReplayGuard? replayGuard = null)
    {
        var token = Environment.GetEnvironmentVariable("WAYL_API_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("WAYL_API_TOKEN is not configured in server-side secrets.");
        var secret = Environment.GetEnvironmentVariable("WAYL_WEBHOOK_SECRET");
        return new WaylPaymentProvider(httpClient, priceCalculator, token, webhookUrl,
            webhookUrl is null ? null : secret, redirectionUrl, replayGuard);
    }

    /// <summary>
    /// Creates or reuses a Wayl hosted checkout using a server-calculated IQD integer total.
    /// ReferenceId is the stable idempotency key and is checked with Wayl before a create call.
    /// </summary>
    public async Task<WaylCheckoutSession> CreateCheckoutAsync(
        WaylCheckoutCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var referenceId = ValidateReferenceId(command.ReferenceId);
        var planCode = ValidateRequired(command.PlanCode, nameof(command.PlanCode), 120);
        var label = ValidateRequired(command.DisplayLabel, nameof(command.DisplayLabel), 160);
        var totalIqd = await priceCalculator.CalculateTotalIqdAsync(planCode, cancellationToken);
        if (totalIqd <= 0)
            throw new InvalidOperationException("The server-side IQD price must be a positive integer.");

        var gate = referenceGates.GetOrAdd(referenceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetLinkAsync(referenceId, cancellationToken);
            if (existing is not null)
            {
                ValidateLinkMatchesOrder(existing, referenceId, totalIqd);
                return ToCheckoutSession(existing, totalIqd, reused: true);
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["env"] = TestEnvironment,
                ["referenceId"] = referenceId,
                ["total"] = totalIqd,
                ["currency"] = "IQD",
                ["customParameter"] = string.Empty,
                ["lineItem"] = new[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["label"] = label,
                        ["amount"] = totalIqd,
                        ["type"] = "increase",
                    },
                },
            };
            if (webhookUrl is not null)
            {
                payload["webhookUrl"] = webhookUrl;
                payload["webhookSecret"] = webhookSecret;
            }
            if (redirectionUrl is not null)
                payload["redirectionUrl"] = redirectionUrl;

            using var request = CreateAuthenticatedRequest(HttpMethod.Post, new Uri(new Uri(ApiBaseUrl), "api/v1/links"));
            request.Content = JsonContent.Create(payload, options: jsonOptions);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            {
                var duplicate = await GetLinkAsync(referenceId, cancellationToken);
                if (duplicate is null)
                    throw new HttpRequestException("Wayl reported a duplicate referenceId but no matching link could be retrieved.");
                ValidateLinkMatchesOrder(duplicate, referenceId, totalIqd);
                return ToCheckoutSession(duplicate, totalIqd, reused: true);
            }
            response.EnsureSuccessStatusCode();
            var created = await ReadLinkResponseAsync(response, cancellationToken);
            ValidateLinkMatchesOrder(created, referenceId, totalIqd);
            return ToCheckoutSession(created, totalIqd, reused: false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Performs an authenticated canonical GET by referenceId. A signed webhook alone never
    /// establishes completion. This adapter reports provider completion only for matching IQD
    /// amount/currency, Complete status, and a non-null completedAt timestamp.
    /// </summary>
    public async Task<WaylPaymentVerification> VerifyPaymentByApiAsync(
        string referenceId,
        int expectedTotalIqd,
        CancellationToken cancellationToken = default)
    {
        referenceId = ValidateReferenceId(referenceId);
        if (expectedTotalIqd <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedTotalIqd));

        var link = await GetLinkAsync(referenceId, cancellationToken);
        if (link is null)
        {
            return new(referenceId, WaylPaymentVerificationOutcome.NotFound, WaylPaymentState.Unknown,
                null, null, null, false, "The authenticated Wayl lookup did not return this referenceId.");
        }

        if (!string.Equals(link.ReferenceId, referenceId, StringComparison.Ordinal)
            || link.TotalIqd != expectedTotalIqd
            || !string.Equals(link.Currency, "IQD", StringComparison.OrdinalIgnoreCase))
        {
            return new(referenceId, WaylPaymentVerificationOutcome.AmountOrCurrencyMismatch, link.State,
                link.TotalIqd, link.Currency, link.CompletedAt, true,
                "Wayl's referenceId, integer total, or currency does not match the server order.");
        }

        if (link.State != WaylPaymentState.Complete || link.CompletedAt is null)
        {
            return new(referenceId, WaylPaymentVerificationOutcome.NotCompleted, link.State,
                link.TotalIqd, link.Currency, link.CompletedAt, true,
                "Wayl has not reported a completed payment with completedAt.");
        }

        return new(referenceId, WaylPaymentVerificationOutcome.ProviderCompleted, link.State,
            link.TotalIqd, link.Currency, link.CompletedAt, true,
            "Wayl's authenticated API reports a matching completed payment; customer entitlements remain disabled in this test-only adapter.");
    }

    /// <summary>
    /// Verifies a webhook HMAC over the exact raw bytes, parses only after signature verification,
    /// and rejects repeated event identifiers. This volatile replay guard is for local tests only.
    /// No credit, subscription, refund, or payment-ledger mutation occurs here.
    /// </summary>
    public WaylWebhookReceipt ReceiveSignedWebhook(ReadOnlyMemory<byte> rawBody, string? signature)
    {
        if (rawBody.Length == 0 || rawBody.Length > MaximumWebhookBytes)
            throw new InvalidOperationException("The Wayl webhook body is empty or exceeds the local size limit.");
        if (webhookSecret is null || !VerifyWebhookSignature(rawBody.Span, signature))
            throw new InvalidOperationException("The Wayl webhook signature is invalid or no signing secret is configured.");

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        var eventId = ReadString(root, "id");
        var referenceId = ReadString(root, "referenceId");
        var eventName = ReadString(root, "event") ?? string.Empty;
        var paymentStatus = ReadString(root, "paymentStatus");
        var refundStatus = ReadString(root, "refundStatus");
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(referenceId))
            throw new InvalidOperationException("A signed Wayl webhook must include stable id and referenceId values.");

        eventId = ValidateRequired(eventId, nameof(eventId), 180);
        referenceId = ValidateReferenceId(referenceId);
        var totalIqd = TryReadInteger(root, "total");
        var state = WaylPaymentStateMapper.Map(eventName, paymentStatus, refundStatus);
        var payloadHash = Convert.ToHexString(SHA256.HashData(rawBody.Span));
        var replayDisposition = replayGuard.Register(eventId, payloadHash);
        return new WaylWebhookReceipt(eventId, referenceId, eventName, paymentStatus, totalIqd,
            state, payloadHash, replayDisposition);
    }

    /// <summary>Computes HMAC-SHA256 over the exact raw body bytes and compares in constant time.</summary>
    public bool VerifyWebhookSignature(ReadOnlySpan<byte> rawBody, string? signature)
    {
        if (webhookSecret is null || string.IsNullOrWhiteSpace(signature) || signature.Length != 64)
            return false;

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var key = Encoding.UTF8.GetBytes(webhookSecret);
        var expected = HMACSHA256.HashData(key, rawBody);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private async Task<WaylLinkSnapshot?> GetLinkAsync(string referenceId, CancellationToken cancellationToken)
    {
        var uri = new Uri(new Uri(ApiBaseUrl), "api/v1/links/" + Uri.EscapeDataString(referenceId));
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, uri);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await ReadLinkResponseAsync(response, cancellationToken);
    }

    private async Task<WaylLinkSnapshot> ReadLinkResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Wayl returned a response without a data object.");

        var referenceId = ReadString(data, "referenceId") ?? throw new InvalidOperationException("Wayl response omitted referenceId.");
        var totalIqd = TryReadInteger(data, "total") ?? throw new InvalidOperationException("Wayl response omitted an integer IQD total.");
        var currency = ReadString(data, "currency") ?? throw new InvalidOperationException("Wayl response omitted currency.");
        var status = ReadString(data, "status");
        var completedAtText = ReadString(data, "completedAt");
        DateTimeOffset? completedAt = DateTimeOffset.TryParse(completedAtText, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedCompletedAt)
            ? parsedCompletedAt
            : null;
        var urlText = ReadString(data, "url");
        Uri? checkoutUrl = null;
        if (!string.IsNullOrWhiteSpace(urlText))
        {
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out checkoutUrl))
                throw new InvalidOperationException("Wayl returned an invalid hosted checkout URL.");
        }

        return new WaylLinkSnapshot(
            referenceId,
            totalIqd,
            currency,
            WaylPaymentStateMapper.Map(null, status),
            checkoutUrl,
            ReadString(data, "id"),
            ReadString(data, "code"),
            completedAt);
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation(ApiAuthenticationHeader, apiToken);
        return request;
    }

    private static void ValidateLinkMatchesOrder(WaylLinkSnapshot link, string referenceId, int totalIqd)
    {
        if (!string.Equals(link.ReferenceId, referenceId, StringComparison.Ordinal)
            || link.TotalIqd != totalIqd
            || !string.Equals(link.Currency, "IQD", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Wayl link data does not match the server-calculated referenceId, integer IQD total, and currency.");
    }

    private static WaylCheckoutSession ToCheckoutSession(WaylLinkSnapshot link, int expectedTotalIqd, bool reused)
    {
        ValidateLinkMatchesOrder(link, link.ReferenceId, expectedTotalIqd);
        if (link.CheckoutUrl is not null
            && (!string.Equals(link.CheckoutUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(link.CheckoutUrl.Host, "checkout.thewayl.com", StringComparison.OrdinalIgnoreCase)
                || link.CheckoutUrl.UserInfo.Length > 0))
            throw new InvalidOperationException("Wayl returned a hosted checkout URL outside the documented HTTPS checkout host.");

        return new WaylCheckoutSession(link.ReferenceId, link.TotalIqd, link.CheckoutUrl,
            link.State, link.LinkId, link.Code, reused);
    }

    private static string ValidateReferenceId(string? value)
    {
        var normalized = ValidateRequired(value, nameof(value), 180);
        if (normalized.Length > 180)
            throw new ArgumentOutOfRangeException(nameof(value), "Wayl referenceId must be at most 180 characters.");
        return normalized;
    }

    private static string ValidateRequired(string? value, string parameterName, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength)
            throw new ArgumentException($"{parameterName} must be non-empty and at most {maximumLength} characters.", parameterName);
        return normalized;
    }

    private static void ValidateHttpsUrl(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length > 0)
            throw new ArgumentException("Wayl webhook and redirection URLs must be absolute HTTPS URLs without embedded credentials.", parameterName);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? TryReadInteger(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private sealed record WaylLinkSnapshot(
        string ReferenceId,
        int TotalIqd,
        string Currency,
        WaylPaymentState State,
        Uri? CheckoutUrl,
        string? LinkId,
        string? Code,
        DateTimeOffset? CompletedAt);
}
