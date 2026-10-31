using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taslim.Api.Payments;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class WaylPaymentProviderTests
{
    private const string ApiToken = "local-test-api-token";
    private const string WebhookSecret = "local-webhook-secret-at-least-10";
    private const string ReferenceId = "taslim-test-order-001";
    private const string HostedUrl = "https://checkout.thewayl.com/payment/action?id=local-test";
    private const string CompletedAt = "2026-10-31T20:00:00.000Z";

    [Fact]
    public async Task Checkout_uses_server_price_integer_IQD_test_env_and_Wayl_hosted_redirect()
    {
        var fakePrice = new FakePriceCalculator(10_000);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            AssertAuthenticated(request);
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            Assert.Equal("test", root.GetProperty("env").GetString());
            Assert.Equal(ReferenceId, root.GetProperty("referenceId").GetString());
            Assert.Equal(10_000, root.GetProperty("total").GetInt32());
            Assert.Equal("IQD", root.GetProperty("currency").GetString());
            Assert.Equal(0, root.GetProperty("customParameter").GetString()!.Length);
            var line = Assert.Single(root.GetProperty("lineItem").EnumerateArray());
            Assert.Equal("Taslim Pro", line.GetProperty("label").GetString());
            Assert.Equal(10_000, line.GetProperty("amount").GetInt32());
            Assert.Equal("increase", line.GetProperty("type").GetString());
            Assert.Equal("https://taslim.example/hooks/wayl", root.GetProperty("webhookUrl").GetString());
            Assert.Equal(WebhookSecret, root.GetProperty("webhookSecret").GetString());
            Assert.Equal("https://taslim.example/thanks", root.GetProperty("redirectionUrl").GetString());
            return LinkResponse(HttpStatusCode.Created, ReferenceId, "10000", "IQD", "Created", HostedUrl);
        });
        using var httpClient = new HttpClient(handler);
        var provider = new WaylPaymentProvider(httpClient, fakePrice, ApiToken,
            "https://taslim.example/hooks/wayl", WebhookSecret, "https://taslim.example/thanks");

        var result = await provider.CreateCheckoutAsync(new WaylCheckoutCommand(ReferenceId, "pro", "Taslim Pro"));

        Assert.Equal("pro", fakePrice.LastPlanCode);
        Assert.Equal(1, fakePrice.CallCount);
        Assert.Equal(10_000, result.TotalIqd);
        Assert.Equal(new Uri(HostedUrl), result.HostedCheckoutUrl);
        Assert.Equal(WaylPaymentState.Created, result.State);
        Assert.False(result.ReusedExistingReference);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, captured => Assert.Equal("https://api.thewayl.com", captured.RequestUri!.GetLeftPart(UriPartial.Authority)));
    }

    [Fact]
    public async Task Existing_referenceId_is_reused_without_a_second_link_create()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            AssertAuthenticated(request);
            return Task.FromResult(LinkResponse(HttpStatusCode.OK, ReferenceId, "10000", "IQD", "Complete", HostedUrl, CompletedAt));
        });
        using var httpClient = new HttpClient(handler);
        var provider = NewProvider(httpClient, new FakePriceCalculator(10_000));

        var result = await provider.CreateCheckoutAsync(new WaylCheckoutCommand(ReferenceId, "pro", "Taslim Pro"));

        Assert.True(result.ReusedExistingReference);
        Assert.Equal(WaylPaymentState.Complete, result.State);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task Concurrent_checkout_replays_for_the_same_referenceId_create_at_most_one_link()
    {
        var created = false;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            AssertAuthenticated(request);
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(created
                    ? LinkResponse(HttpStatusCode.OK, ReferenceId, "10000", "IQD", "Created", HostedUrl)
                    : new HttpResponseMessage(HttpStatusCode.NotFound));

            Assert.Equal(HttpMethod.Post, request.Method);
            created = true;
            return Task.FromResult(LinkResponse(HttpStatusCode.Created, ReferenceId, "10000", "IQD", "Created", HostedUrl));
        });
        using var httpClient = new HttpClient(handler);
        var provider = NewProvider(httpClient, new FakePriceCalculator(10_000));
        var command = new WaylCheckoutCommand(ReferenceId, "pro", "Taslim Pro");

        var sessions = await Task.WhenAll(provider.CreateCheckoutAsync(command), provider.CreateCheckoutAsync(command));

        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Post));
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(sessions, session => Assert.Equal(new Uri(HostedUrl), session.HostedCheckoutUrl));
        Assert.Single(sessions, session => session.ReusedExistingReference);
    }

    [Fact]
    public async Task Duplicate_reference_conflict_is_reconciled_by_authenticated_lookup()
    {
        var linkExists = false;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            AssertAuthenticated(request);
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(linkExists
                    ? LinkResponse(HttpStatusCode.OK, ReferenceId, "10000", "IQD", "Created", HostedUrl)
                    : new HttpResponseMessage(HttpStatusCode.NotFound));

            linkExists = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        });
        using var httpClient = new HttpClient(handler);
        var provider = NewProvider(httpClient, new FakePriceCalculator(10_000));

        var result = await provider.CreateCheckoutAsync(new WaylCheckoutCommand(ReferenceId, "pro", "Taslim Pro"));

        Assert.True(result.ReusedExistingReference);
        Assert.Equal(new Uri(HostedUrl), result.HostedCheckoutUrl);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Checkout_refuses_nonpositive_server_calculated_price_before_any_HTTP()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new Xunit.Sdk.XunitException("No API request should be sent."));
        using var httpClient = new HttpClient(handler);
        var provider = NewProvider(httpClient, new FakePriceCalculator(0));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateCheckoutAsync(
            new WaylCheckoutCommand(ReferenceId, "pro", "Taslim Pro")));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Authenticated_API_verification_requires_matching_amount_currency_and_complete_timestamp_and_never_grants_credits()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/api/v1/links/" + ReferenceId, request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            AssertAuthenticated(request);
            return Task.FromResult(LinkResponse(HttpStatusCode.OK, ReferenceId, "10000", "IQD", "Complete", HostedUrl, CompletedAt));
        });
        using var httpClient = new HttpClient(handler);
        var provider = NewProvider(httpClient, new FakePriceCalculator(10_000));

        var verification = await provider.VerifyPaymentByApiAsync(ReferenceId, 10_000);

        Assert.True(verification.ApiLookupSucceeded);
        Assert.Equal(WaylPaymentVerificationOutcome.ProviderCompleted, verification.Outcome);
        Assert.True(verification.ProviderReportsCompletedPayment);
        Assert.False(verification.CanGrantCredits);
        Assert.Equal(10_000, verification.TotalIqd);
        Assert.Equal("IQD", verification.Currency);
        Assert.Equal(DateTimeOffset.Parse(CompletedAt), verification.CompletedAt);
    }

    [Theory]
    [InlineData("9999", "IQD", "Complete", "2026-10-31T20:00:00.000Z", WaylPaymentVerificationOutcome.AmountOrCurrencyMismatch)]
    [InlineData("10000", "USD", "Complete", "2026-10-31T20:00:00.000Z", WaylPaymentVerificationOutcome.AmountOrCurrencyMismatch)]
    [InlineData("10000", "IQD", "Processing", null, WaylPaymentVerificationOutcome.NotCompleted)]
    [InlineData("10000", "IQD", "Delivered", "2026-10-31T20:00:00.000Z", WaylPaymentVerificationOutcome.NotCompleted)]
    [InlineData("10000", "IQD", "Complete", null, WaylPaymentVerificationOutcome.NotCompleted)]
    public async Task Verification_rejects_unmatched_or_not_completed_provider_states(
        string total, string currency, string status, string? completedAt, WaylPaymentVerificationOutcome expected)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            LinkResponse(HttpStatusCode.OK, ReferenceId, total, currency, status, HostedUrl, completedAt)));
        using var httpClient = new HttpClient(handler);
        var provider = NewProvider(httpClient, new FakePriceCalculator(10_000));

        var verification = await provider.VerifyPaymentByApiAsync(ReferenceId, 10_000);

        Assert.Equal(expected, verification.Outcome);
        Assert.False(verification.CanGrantCredits);
    }

    [Fact]
    public void Webhook_signature_uses_exact_raw_body_and_rejects_invalid_signatures()
    {
        var provider = NewProvider(new HttpClient(new StubHttpMessageHandler((_, _) => throw new Xunit.Sdk.XunitException("Webhook verification must not make HTTP calls."))),
            new FakePriceCalculator(10_000));
        var rawBody = Encoding.UTF8.GetBytes("{\"id\":\"evt-1\",\"event\":\"order.updated\",\"referenceId\":\"taslim-test-order-001\",\"paymentStatus\":\"Complete\",\"total\":10000}");
        var signature = Sign(rawBody);
        var changedRawBody = Encoding.UTF8.GetBytes("{ \"id\":\"evt-1\",\"event\":\"order.updated\",\"referenceId\":\"taslim-test-order-001\",\"paymentStatus\":\"Complete\",\"total\":10000}");

        Assert.True(provider.VerifyWebhookSignature(rawBody, signature));
        Assert.False(provider.VerifyWebhookSignature(changedRawBody, signature));
        Assert.False(provider.VerifyWebhookSignature(rawBody, "invalid"));
        Assert.Throws<InvalidOperationException>(() => provider.ReceiveSignedWebhook(changedRawBody, signature));
    }

    [Fact]
    public void Signed_webhook_requires_signature_and_replay_guard_flags_duplicate_and_event_id_collision()
    {
        var provider = NewProvider(new HttpClient(new StubHttpMessageHandler((_, _) => throw new Xunit.Sdk.XunitException("Webhook parsing must not make HTTP calls."))),
            new FakePriceCalculator(10_000));
        var firstBody = Encoding.UTF8.GetBytes("{\"id\":\"evt-2\",\"event\":\"order.updated\",\"referenceId\":\"taslim-test-order-001\",\"paymentStatus\":\"Complete\",\"total\":10000}");
        var duplicateBody = firstBody.ToArray();
        var conflictingBody = Encoding.UTF8.GetBytes("{ \"id\":\"evt-2\",\"event\":\"order.updated\",\"referenceId\":\"taslim-test-order-001\",\"paymentStatus\":\"Complete\",\"total\":10000}");

        var first = provider.ReceiveSignedWebhook(firstBody, Sign(firstBody));
        var duplicate = provider.ReceiveSignedWebhook(duplicateBody, Sign(duplicateBody));
        var conflict = provider.ReceiveSignedWebhook(conflictingBody, Sign(conflictingBody));

        Assert.True(first.SignatureVerified);
        Assert.True(first.RequiresAuthenticatedApiVerification);
        Assert.Equal(WaylReplayDisposition.FirstSeen, first.ReplayDisposition);
        Assert.Equal(WaylReplayDisposition.Duplicate, duplicate.ReplayDisposition);
        Assert.Equal(WaylReplayDisposition.EventIdConflict, conflict.ReplayDisposition);
    }

    [Fact]
    public void Invalid_signature_does_not_consume_event_id_in_replay_guard()
    {
        var provider = NewProvider(new HttpClient(new StubHttpMessageHandler((_, _) => throw new Xunit.Sdk.XunitException("Webhook parsing must not make HTTP calls."))),
            new FakePriceCalculator(10_000));
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt-3\",\"event\":\"order.updated\",\"referenceId\":\"taslim-test-order-001\",\"paymentStatus\":\"Pending\",\"total\":10000}");

        Assert.Throws<InvalidOperationException>(() => provider.ReceiveSignedWebhook(body, "00"));
        var accepted = provider.ReceiveSignedWebhook(body, Sign(body));

        Assert.Equal(WaylReplayDisposition.FirstSeen, accepted.ReplayDisposition);
        Assert.Equal(WaylPaymentState.Pending, accepted.State);
    }

    [Theory]
    [InlineData("Requested", WaylPaymentState.RefundRequested)]
    [InlineData("Refunded", WaylPaymentState.Refunded)]
    [InlineData("Rejected", WaylPaymentState.RefundRejected)]
    [InlineData("Cancelled", WaylPaymentState.RefundCancelled)]
    public void Refund_statuses_are_modeled_without_issuing_refund_requests(string status, WaylPaymentState expected)
    {
        Assert.Equal(expected, WaylPaymentStateMapper.Map("refund.updated", null, status));
    }

    [Theory]
    [InlineData("chargeback.opened", "", WaylPaymentState.ChargebackOpen)]
    [InlineData("chargeback.updated", "won", WaylPaymentState.ChargebackWon)]
    [InlineData("dispute.updated", "lost", WaylPaymentState.ChargebackLost)]
    [InlineData("dispute.updated", "reversed", WaylPaymentState.ChargebackReversed)]
    public void Chargeback_and_dispute_states_are_non_success_states(string eventName, string status, WaylPaymentState expected)
    {
        Assert.Equal(expected, WaylPaymentStateMapper.Map(eventName, status));
        Assert.NotEqual(WaylPaymentState.Complete, expected);
    }

    [Fact]
    public async Task Checkout_response_with_untrusted_redirect_host_is_rejected()
    {
        var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : LinkResponse(HttpStatusCode.Created, ReferenceId, "10000", "IQD", "Created", "https://attacker.example/pay")));
        using var httpClient = new HttpClient(handler);
        var provider = NewProvider(httpClient, new FakePriceCalculator(10_000));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateCheckoutAsync(
            new WaylCheckoutCommand(ReferenceId, "pro", "Taslim Pro")));
    }

    private static WaylPaymentProvider NewProvider(HttpClient httpClient, FakePriceCalculator priceCalculator) =>
        new(httpClient, priceCalculator, ApiToken, "https://taslim.example/hooks/wayl", WebhookSecret);

    private static HttpResponseMessage LinkResponse(
        HttpStatusCode statusCode,
        string referenceId,
        string total,
        string currency,
        string paymentStatus,
        string? url,
        string? completedAt = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["referenceId"] = referenceId,
            ["id"] = "cmlink-local-test",
            ["code"] = "LOCALTEST",
            ["total"] = total,
            ["currency"] = currency,
            ["status"] = paymentStatus,
            ["paymentMethod"] = paymentStatus == "Complete" ? "Card" : null,
            ["completedAt"] = completedAt,
            ["createdAt"] = "2026-10-31T19:59:00.000Z",
            ["updatedAt"] = completedAt ?? "2026-10-31T19:59:00.000Z",
        };
        if (url is not null)
            data["url"] = url;
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { data, message = "local fixture" }), Encoding.UTF8, "application/json"),
        };
    }

    private static void AssertAuthenticated(HttpRequestMessage request)
    {
        Assert.True(request.Headers.TryGetValues(WaylPaymentProvider.ApiAuthenticationHeader, out var values));
        Assert.Equal(ApiToken, Assert.Single(values!));
    }

    private static string Sign(byte[] rawBody) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), rawBody)).ToLowerInvariant();

    private sealed class FakePriceCalculator(int totalIqd) : IWaylIqdPriceCalculator
    {
        public string? LastPlanCode { get; private set; }
        public int CallCount { get; private set; }

        public ValueTask<int> CalculateTotalIqdAsync(string planCode, CancellationToken cancellationToken = default)
        {
            LastPlanCode = planCode;
            CallCount++;
            return ValueTask.FromResult(totalIqd);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? RequestUri, string? ApiToken);

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<CapturedRequest> requests = new();
        public IReadOnlyList<CapturedRequest> Requests => requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.TryGetValues(WaylPaymentProvider.ApiAuthenticationHeader, out var values)
                ? values.SingleOrDefault()
                : null;
            requests.Enqueue(new CapturedRequest(request.Method, request.RequestUri, token));
            return await responder(request, cancellationToken);
        }
    }
}
