using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Files;
using Taslim.Api.Music;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MubertMusicGenerationProviderTests
{
    [Fact]
    public async Task Creates_polls_and_downloads_a_track_using_documented_contract()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-1\",\"session_id\":\"session-1\"}}"),
                2 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"pending\",\"url\":null}]}}"),
                3 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Signature=test\"}]}}"),
                _ => Binary(HttpStatusCode.OK, "ID3-test-audio", "audio/mpeg"),
            });
        var provider = CreateProvider(handler, pollIntervalMilliseconds: 100);

        var result = await provider.GenerateAsync(Input());

        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal("mp3", result.Format);
        Assert.Equal("ID3-test-audio", Encoding.UTF8.GetString(result.Content.Span));
        Assert.Equal(60, result.DurationSeconds);
        Assert.Contains("providerOperationId", result.Usage.SafeMetadataJson);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("customer", handler.Requests[0].Headers.GetValues("customer-id").Single());
        Assert.Equal("token", handler.Requests[0].Headers.GetValues("access-token").Single());
        var requestJson = handler.RequestBodies[0];
        Assert.Contains("\"duration\":60", requestJson);
        Assert.Contains("\"format\":\"mp3\"", requestJson);
        Assert.Contains("Description", requestJson);
    }

    [Fact]
    public async Task Does_not_retry_billable_track_creation_post()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                2 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-2\",\"session_id\":\"session-2\"}}"),
                3 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3\"}]}}"),
                _ => Binary(HttpStatusCode.OK, "ID3-audio", "audio/mpeg"),
            });
        var provider = CreateProvider(handler, retryAttempts: 1, retryDelayMilliseconds: 25);

        await Assert.ThrowsAsync<MusicProviderUnavailableException>(() => provider.GenerateAsync(Input()));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Missing_credentials_are_unavailable_without_making_a_request()
    {
        var handler = new MubertHandler((_, _) => throw new InvalidOperationException("request should not be sent"));
        var provider = CreateProvider(handler, customerId: "", accessToken: "");

        await Assert.ThrowsAsync<MusicProviderUnavailableException>(() => provider.GenerateAsync(Input()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Non_transient_provider_failure_is_mapped_without_exposing_provider_payload()
    {
        var handler = new MubertHandler((_, _) => Json(HttpStatusCode.BadRequest, "{\"error\":\"private provider payload\"}"));
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<MusicProviderFailureException>(() => provider.GenerateAsync(Input()));

        Assert.DoesNotContain("private provider payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bounded_polling_of_an_accepted_track_stops_without_a_second_billable_submission()
    {
        // The create was accepted (track-3 exists on Mubert's side) and the bounded polling window ran
        // out while it still reported processing. Billing state is unknown, so this is NOT the retryable
        // timeout class: RetryAsync re-runs CreateAsync, which would POST a second paid track. The job
        // stops on the unconfirmed-submission class instead - the track id proves the submission was
        // accepted, but no customer copy may claim a finished track, because polling never observed one.
        // Exactly one billable POST was sent and no audio is fabricated either way.
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-3\",\"session_id\":\"session-3\"}}"),
                _ => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"processing\",\"url\":null}]}}"),
            });
        var provider = CreateProvider(handler, maxPollAttempts: 2, pollIntervalMilliseconds: 100);

        var failure = await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Caller_cancellation_stops_polling_and_is_not_converted_to_timeout()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber == 1
                ? Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-4\",\"session_id\":\"session-4\"}}")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new BlockingStream()),
                });
        var provider = CreateProvider(handler, pollIntervalMilliseconds: 100);
        using var cancellation = new CancellationTokenSource();
        var generation = provider.GenerateAsync(Input(), cancellation.Token);
        await handler.WaitForRequestAsync(2);
        cancellation.Cancel();

        // F-36: the create POST was already answered with a track id, so this is not an ordinary
        // cancellation - it is the accepted-submission posture, whose retry is refused downstream.
        var cancellationFault = await Assert.ThrowsAsync<MusicProviderCancellationWithAcceptedSubmissionException>(() => generation);
        Assert.False(cancellationFault.OutputConfirmedComplete);
        Assert.Equal("track-4", cancellationFault.ProviderOperationId);
    }

    [Fact]
    public async Task Oversized_download_is_rejected_before_a_music_result_is_returned()
    {
        var handler = CompleteTrackHandler(_ => Binary(HttpStatusCode.OK, "ID3-too-large", "audio/mpeg"));
        var provider = CreateProvider(handler, maxOutputBytes: 8);

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Html_body_with_an_audio_mime_type_is_rejected()
    {
        var handler = CompleteTrackHandler(_ => Binary(HttpStatusCode.OK, "<html>provider error</html>", "audio/mpeg"));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Mismatched_download_mime_type_is_rejected()
    {
        var handler = CompleteTrackHandler(_ => Binary(HttpStatusCode.OK, "ID3-audio", "audio/wav"));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Truncated_download_is_rejected_when_content_length_is_declared()
    {
        var handler = CompleteTrackHandler(_ => BinaryWithDeclaredLength(HttpStatusCode.OK, "ID3", "audio/mpeg", 10));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
    }

    [Fact]
    public async Task Redirect_to_private_ip_is_rejected_before_following_the_redirect()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-private-redirect\",\"session_id\":\"session\"}}"),
                2 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://1.1.1.1/track.mp3\"}]}}"),
                3 => Redirect(HttpStatusCode.Found, "https://127.0.0.1/metadata"),
                _ => throw new InvalidOperationException("private redirect must not be requested"),
            });
        var provider = CreateProvider(handler, urlPolicy: new ProviderUrlPolicy());

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
        Assert.Equal(3, handler.Requests.Count);
    }

    // The redirect hop is where a widened deny set earns its keep: the guard runs again on every
    // Location: value, so a provider that bounces to a special-use target must be refused before
    // the client ever issues that request. 2002::/16 is the case a private/metadata sweep misses
    // because the literal looks routable until the embedded IPv4 is considered.
    [Theory]
    [InlineData("https://[2002:7f00:1::]/track.mp3")]
    [InlineData("https://192.88.99.1/track.mp3")]
    [InlineData("https://[::a9fe:a9fe]/track.mp3")]
    public async Task Redirect_to_special_use_target_is_rejected_before_it_is_requested(string location)
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-reserved-redirect\",\"session_id\":\"session\"}}"),
                2 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://1.1.1.1/track.mp3\"}]}}"),
                3 => Redirect(HttpStatusCode.Found, location),
                _ => throw new InvalidOperationException("reserved redirect target must not be requested"),
            });
        var provider = CreateProvider(handler, urlPolicy: new ProviderUrlPolicy());

        await Assert.ThrowsAsync<MusicOutputInvalidException>(() => provider.GenerateAsync(Input()));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Hanging_download_after_the_track_completed_is_a_retrieval_failure_not_a_retryable_timeout()
    {
        // The status response carried a track URL, so Mubert has produced - and billed - the track.
        // The internal provider timeout firing underneath the artifact read used to surface as
        // MusicProviderTimeoutException, which the job layer treats as retryable: a retry would POST a
        // second paid track and orphan the finished one. Post-completion it is the terminal retrieval
        // class instead, which MusicPostBillingRetrievalIsolationTests proves blocks the retry.
        var handler = CompleteTrackHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream()),
        });
        var provider = CreateProvider(handler, providerTimeoutSeconds: 1);

        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        // create POST, status GET, one artifact GET; the timeout ends the attempt without a re-POST.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Caller_cancellation_while_reading_a_completed_track_still_propagates_as_cancellation()
    {
        // The completion-known classification is scoped to the internal timeout only. When the caller
        // cancels, the job is cancelled and its usage finalises as cancelled; converting that into a
        // retrieval failure would misreport a deliberate abort as a provider fault.
        var handler = CompleteTrackHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream()),
        });
        var provider = CreateProvider(handler);
        using var cancellation = new CancellationTokenSource();
        var generation = provider.GenerateAsync(Input(), cancellation.Token);
        await handler.WaitForRequestAsync(3);
        cancellation.Cancel();

        // F-36: still a cancellation, and still exactly one billable POST - but the typed one, because
        // the finished track was never delivered. The worker records
        // MUSIC_CANCELLED_UNDELIVERED_OUTPUT, which RetryAsync refuses with the retrieval-failure
        // posture instead of generating a second paid track next to the finished one.
        var cancellationFault = await Assert.ThrowsAsync<MusicProviderCancellationWithAcceptedSubmissionException>(() => generation);
        Assert.True(cancellationFault.OutputConfirmedComplete);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task User_cancellation_of_an_accepted_track_leaves_exactly_one_billable_post()
    {
        // The reported gap (F-36), reproduced at the provider boundary: Mubert accepts the paid
        // generation (the create POST returns a track id), the status poll is still blocked, and the
        // MEMBER cancels. This integration never calls a provider-side cancel, so the accepted track may
        // still finish and may still bill. Before the fix the cancellation escaped as a plain
        // OperationCanceledException, the job was terminalized as MUSIC_CANCELLED, and RetryAsync -
        // which permits cancelled jobs on the assumption that nothing is in flight - would have run
        // CreateAsync again: a second paid POST while the first track was still generating. The provider
        // now surfaces the accepted outcome on the cancellation itself, and the POST count is the money
        // assertion. The job-level form of this test is
        // MusicRetryPostureIsolationTests.Member_cancelling_an_accepted_music_job_is_refused_on_retry_and_never_posts_a_second_generation.
        var handler = new MubertHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-user-cancel\",\"session_id\":\"session-user-cancel\"}}"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingStream()),
            },
        });
        var provider = CreateProvider(handler, pollIntervalMilliseconds: 50);
        using var cancellation = new CancellationTokenSource();
        var generation = provider.GenerateAsync(Input(), cancellation.Token);
        await handler.WaitForRequestAsync(2);
        cancellation.Cancel();

        var failure = await Assert.ThrowsAsync<MusicProviderCancellationWithAcceptedSubmissionException>(() => generation);
        Assert.False(failure.OutputConfirmedComplete);
        Assert.Equal("track-user-cancel", failure.ProviderOperationId);
        // Nothing about the accepted operation is asserted as complete, and the provider was billed for
        // at most the one generation it accepted.
        Assert.DoesNotContain("generated", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task User_cancellation_before_the_create_is_answered_stays_an_ordinary_cancellation()
    {
        // The required contrast case: while the billable create POST is still unanswered, acceptance is
        // NOT established, so this stays the deliberate-abort posture the retry path treats as safe. The
        // typed unconfirmed cancellation must not leak into the pre-submission stage, or a member who
        // cancels too early would be stranded behind a refusal for a job that never reached anyone.
        var handler = new MubertHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream()),
        });
        var provider = CreateProvider(handler);
        using var cancellation = new CancellationTokenSource();
        var generation = provider.GenerateAsync(Input(), cancellation.Token);
        await handler.WaitForRequestAsync(1);
        cancellation.Cancel();

        var failure = await Record.ExceptionAsync(() => generation);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.IsNotType<MusicProviderCancellationWithAcceptedSubmissionException>(failure);
    }

    [Fact]
    public async Task Timeout_while_an_accepted_track_is_still_pending_is_terminal_not_a_retryable_timeout()
    {
        // The create POST was answered with a track id, so Mubert has accepted a paid generation; the
        // status GET then never completes. Billing is unknown at that point, which is exactly why the
        // retryable class is unsafe: RetryAsync would re-run CreateAsync and POST a second track while
        // the accepted one may still be running. The job therefore stops on the unconfirmed-submission
        // class - acceptance is proven, completion is not - with exactly one billable POST sent and the
        // accepted id logged for reconciliation (see the provider's providerSubmissionAccepted branch).
        var handler = new MubertHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-pre-timeout\",\"session_id\":\"session\"}}"),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingStream()),
            },
        });
        var provider = CreateProvider(handler, providerTimeoutSeconds: 1);

        var failure = await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task A_status_response_body_fault_after_acceptance_preserves_the_original_operation()
    {
        var handler = new MubertHandler((_, number) => number == 1
            ? Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"original-track\"}}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new IoFaultingStream()) });
        var provider = CreateProvider(handler);
        string? acceptedId = null;

        await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(
            Input(), CancellationToken.None, id => { acceptedId = id; return Task.CompletedTask; }));

        Assert.Equal("original-track", acceptedId);
        Assert.Collection(handler.Requests, post => Assert.Equal(HttpMethod.Post, post.Method),
            get => { Assert.Equal(HttpMethod.Get, get.Method); Assert.EndsWith("/tracks/original-track", get.RequestUri!.AbsolutePath); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lost_or_incomplete_create_response_is_unconfirmed(bool lostResponse)
    {
        var handler = new MubertHandler((_, _) =>
            lostResponse ? throw new HttpRequestException("synthetic lost create response")
                : Json(HttpStatusCode.OK, "{}"));
        var provider = CreateProvider(handler);
        var dispatches = 0;
        var acceptances = 0;

        await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(
            Input(), CancellationToken.None, _ => { acceptances++; return Task.CompletedTask; },
            () => { dispatches++; return Task.CompletedTask; }));

        Assert.Equal(1, dispatches);
        Assert.Equal(0, acceptances);
        Assert.Equal(HttpMethod.Post, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task Timeout_while_reading_the_create_response_is_unconfirmed_and_never_reposts()
    {
        var handler = new MubertHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream()),
        });
        var provider = CreateProvider(handler, providerTimeoutSeconds: 1);

        await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(Input()));
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Repeated_transient_status_failures_after_an_accepted_track_are_unconfirmed_not_a_retryable_outage()
    {
        // Mubert accepted the track (the create POST returned an id) and the status endpoint then keeps
        // answering 503 until the bounded GET retries run out. The helper's exhaustion is a
        // MusicProviderUnavailableException, which the job layer reports as MUSIC_PROVIDER_UNAVAILABLE -
        // a retryable code whose replay would POST a second paid track while the accepted one may still
        // be running and billing. The accepted-stage guard must catch it, and the reported message must
        // not claim a finished track, because polling never saw one.
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-503\",\"session_id\":\"session-503\"}}"),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"private provider payload\"}"),
                },
            });
        var provider = CreateProvider(handler, maxPollAttempts: 2, pollIntervalMilliseconds: 100, retryAttempts: 1);

        var failure = await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.IsNotType<MusicProviderTimeoutException>(failure);
        Assert.DoesNotContain("generated", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private provider payload", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        // Retries were spent on the idempotent status GET only; the billable POST was never repeated.
        Assert.True(handler.Requests.Count > 2, $"expected retried status polling, saw {handler.Requests.Count} requests");
    }

    [Fact]
    public async Task Rate_limited_status_answer_after_an_accepted_track_is_unconfirmed_not_a_retryable_rate_limit()
    {
        // The throttle shape of the same window: Mubert accepted the track and the status endpoint then
        // answers 429 until the bounded GET retries run out. A rate limit is a property of the REQUEST,
        // not of the generation, so its public code is retryable by design - and a replay would post a
        // second paid track next to the accepted one. Mubert's helper reports a throttled status answer
        // as the unavailable class, which the accepted-stage guard catches, so the outcome is reported
        // as unconfirmed. This pins that disposition by name: a later change that starts reporting 429
        // with the dedicated rate-limit class must keep this answer, not reopen the replay path.
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-429\",\"session_id\":\"session-429\"}}"),
                _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":\"private provider payload\"}"),
                },
            });
        var provider = CreateProvider(handler, maxPollAttempts: 2, pollIntervalMilliseconds: 100, retryAttempts: 1);
        var failure = await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(Input()));
        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.DoesNotContain("private provider payload", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generated", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }
    [Fact]
    public async Task Non_transient_status_failure_after_an_accepted_track_is_unconfirmed_not_a_generic_fault()
    {
        // A status answer the helper classifies as a plain provider failure falls through to the generic
        // MUSIC_GENERATION_FAILED code, which is equally retryable and would also replay the paid POST.
        // After acceptance the outcome is unknown, not proven absent.
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-400\",\"session_id\":\"session-400\"}}"),
                _ => Json(HttpStatusCode.BadRequest, "{\"error\":\"private provider payload\"}"),
            });
        var provider = CreateProvider(handler, maxPollAttempts: 2, pollIntervalMilliseconds: 100, retryAttempts: 1);

        var failure = await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.DoesNotContain("private provider payload", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Status_envelope_without_track_data_after_an_accepted_track_is_unconfirmed_not_a_generic_fault()
    {
        // A control for the sibling shape: the status body parses as the documented envelope but carries
        // no data, so the accepted track's outcome is still unknown. Left as the generic provider failure
        // this would fall through to MUSIC_GENERATION_FAILED, another retryable code that replays the
        // paid POST.
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-empty\",\"session_id\":\"session-empty\"}}"),
                _ => Json(HttpStatusCode.OK, "{\"data\":null}"),
            });
        var provider = CreateProvider(handler, maxPollAttempts: 2, pollIntervalMilliseconds: 100);

        var failure = await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.DoesNotContain("generated", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Unreadable_status_payload_after_an_accepted_track_is_unconfirmed_not_a_generic_fault()
    {
        // The status call succeeded but its body is not the documented envelope, so nothing is known
        // about the accepted track: unconfirmed posture rather than a retryable generic fault.
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-garbage\",\"session_id\":\"session-garbage\"}}"),
                _ => Json(HttpStatusCode.OK, "this is not json"),
            });
        var provider = CreateProvider(handler, maxPollAttempts: 2, pollIntervalMilliseconds: 100);

        var failure = await Assert.ThrowsAsync<MusicProviderSubmissionUnconfirmedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Download_transport_failure_is_normalized_to_provider_unavailable()
    {
        var handler = new MubertHandler(
            (_, requestNumber) => requestNumber switch
            {
                1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-transport\",\"session_id\":\"session\"}}"),
                2 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3\"}]}}"),
                _ => throw new HttpRequestException("synthetic download transport failure"),
            });
        // retryAttempts: 0 pins the re-fetch budget so this proves the immediate classification.
        var provider = CreateProvider(handler, maxPollAttempts: 1, retryAttempts: 0);

        // The track was already created and reported complete, so a failed artifact download is a
        // post-billing retrieval failure, not an outage inviting a second paid track request.
        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));
        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Download_stream_io_failure_after_headers_is_normalized_to_provider_unavailable()
    {
        // The request succeeds and headers arrive, then the body stream faults with IOException
        // (SocketException inherits from it). This is the read-stage path, not the request-stage
        // path covered by Download_transport_failure_is_normalized_to_provider_unavailable.
        var handler = CompleteTrackHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new IoFaultingStream()),
        });
        var provider = CreateProvider(handler, maxPollAttempts: 1, retryAttempts: 0);

        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));
        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
    }

    [Fact]
    public async Task Completed_track_is_re_fetched_with_a_get_without_a_second_billable_track()
    {
        // The first artifact GET faults mid-body. The track is already complete and billed, so the
        // provider must recover it with the same GET rather than POSTing another track.
        var handler = CompleteTrackHandler(requestNumber => requestNumber == 3
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new IoFaultingStream()),
            }
            : Binary(HttpStatusCode.OK, "ID3-recovered-audio", "audio/mpeg"));
        var provider = CreateProvider(handler, maxPollAttempts: 1, retryAttempts: 1, retryDelayMilliseconds: 1);

        var result = await provider.GenerateAsync(Input());

        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.Contains("download.example", handler.Requests[3].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persistent_artifact_transport_fault_exhausts_the_budget_without_a_second_track()
    {
        var handler = CompleteTrackHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new IoFaultingStream()),
        });
        var provider = CreateProvider(handler, maxPollAttempts: 1, retryAttempts: 2, retryDelayMilliseconds: 1);

        // Exhaustion of the bounded artifact GETs leaves the billed track undelivered; the generic
        // provider-outage class would invite a retry that creates a second paid track.
        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));
        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);

        // create POST, status GET, then the artifact GET plus two bounded re-fetches
        Assert.Equal(5, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Artifact_http_500_after_completion_stays_off_the_second_billable_track()
    {
        // A server error on the artifact GET used to raise MusicProviderFailureException, which is
        // not a MusicProviderUnavailableException, so the transport-recovery wrapper never saw it and
        // the job fell through to the generic retry-inviting music code: a retry would POST a second
        // paid track and orphan the completed one. A transient status must instead spend its budget
        // on re-GETs of the same URL and end at the post-billing retrieval terminal.
        var handler = CompleteTrackHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = CreateProvider(handler, maxPollAttempts: 1, retryAttempts: 1, retryDelayMilliseconds: 1);

        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        // create POST, status GET, then the artifact GET plus one bounded re-fetch.
        Assert.Equal(4, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.All(
            handler.Requests.Skip(2),
            request => Assert.Contains("download.example", request.RequestUri!.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Artifact_permanent_status_is_terminal_without_consuming_the_refetch_budget()
    {
        // A not-found artifact will not be healed by asking again, and it must still not be reported
        // as the generic provider-failure class that invites a retry.
        var handler = CompleteTrackHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = CreateProvider(handler, maxPollAttempts: 1, retryAttempts: 2, retryDelayMilliseconds: 1);

        var failure = await Assert.ThrowsAsync<MusicOutputRetrievalFailedException>(() => provider.GenerateAsync(Input()));

        Assert.IsAssignableFrom<MusicProviderUnavailableException>(failure);
        // create POST, status GET, one artifact GET: no re-fetch and no second track.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Accepted_track_id_reaches_the_submission_sink_before_the_first_status_request()
    {
        // F-36.1 ordering contract. The durable accepted-submission record has to exist BEFORE the
        // polling window that can lose the process, so the sink must fire between the billable create
        // POST and the first status GET. Evidence written only on completion would be useless to lease
        // recovery, which is precisely the path that runs after a worker died mid-generation.
        var sinkCalls = new List<string?>();
        var requestsWhenSinkRan = -1;
        var handler = new MubertHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-sink-order\",\"session_id\":\"session\"}}"),
            2 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3\"}]}}"),
            _ => Binary(HttpStatusCode.OK, "ID3-test-audio", "audio/mpeg"),
        });
        var provider = CreateProvider(handler, pollIntervalMilliseconds: 1);
        var result = await provider.GenerateAsync(
            Input(),
            CancellationToken.None,
            operationId =>
            {
                sinkCalls.Add(operationId);
                requestsWhenSinkRan = handler.Requests.Count;
                return Task.CompletedTask;
            });
        Assert.Equal("audio/mpeg", result.ContentType);
        // Exactly once, carrying the provider's own accepted operation id, and with only the create
        // POST behind it: the first status request had not been issued yet.
        Assert.Equal(new[] { "track-sink-order" }, sinkCalls);
        Assert.Equal(1, requestsWhenSinkRan);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        Assert.True(handler.Requests.Count >= 3, "polling must resume after the evidence was reported");
    }
    [Fact]
    public async Task Unpersistable_submission_evidence_fails_closed_instead_of_looking_unsubmitted()
    {
        // The worker's sink raises MusicProviderSubmissionUnconfirmedException when the durable write
        // cannot land (blank operation id, no current attempt row, or a database fault). That signal has
        // to leave the provider unchanged: "we could not record the acceptance" is not "nothing was
        // submitted", and only the unconfirmed class is terminal with automatic re-dispatch suppressed
        // and the manual retry refused. Swallowing it here would put the job back on a retryable path
        // and cost the customer a second paid track.
        var handler = new MubertHandler((_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-sink-fault\",\"session_id\":\"session\"}}"),
            _ => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3\"}]}}"),
        });
        var provider = CreateProvider(handler, pollIntervalMilliseconds: 1);
        var failure = await Record.ExceptionAsync(() => provider.GenerateAsync(
            Input(),
            CancellationToken.None,
            _ => Task.FromException(new MusicProviderSubmissionUnconfirmedException())));
        Assert.IsType<MusicProviderSubmissionUnconfirmedException>(failure);
        // Fail closed means fail now: no poll was issued and no second billable track was created.
        Assert.Single(handler.Requests);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Dispatch_marker_that_cannot_be_written_stops_the_billable_create_request()
    {
        // F-36.1b ordering proof, at the transport seam: the worker's pre-dispatch stamp fails closed with
        // this exact exception, and the paid create must never be issued behind it. An empty request list is
        // the assertion that matters - it means the vendor never saw a generation request at all, so there
        // is nothing to be charged for and nothing to reconcile.
        var handler = new MubertHandler((_, _) => Json(HttpStatusCode.InternalServerError, "{}"));
        var provider = CreateProvider(handler);
        var failure = await Record.ExceptionAsync(() => provider.GenerateAsync(
            Input(),
            CancellationToken.None,
            null,
            () => Task.FromException(new MusicProviderUnavailableException())));
        Assert.IsType<MusicProviderUnavailableException>(failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Dispatch_marker_is_handed_over_before_any_request_leaves_the_provider()
    {
        // The other half of the same guarantee: when the stamp succeeds, the marker was already durable by
        // the time the create went out, so a crash after this point leaves a sentinel rather than silence.
        var seenAtStamp = -1;
        var handler = new MubertHandler((_, _) => Json(HttpStatusCode.InternalServerError, "{}"));
        var provider = CreateProvider(handler);
        await Record.ExceptionAsync(() => provider.GenerateAsync(
            Input(),
            CancellationToken.None,
            null,
            () =>
            {
                seenAtStamp = handler.Requests.Count;
                return Task.CompletedTask;
            }));
        Assert.Equal(0, seenAtStamp);
        Assert.True(handler.Requests.Count >= 1, "the create was never attempted once the marker was durable");
    }

    [Fact]
    public async Task Quota_free_track_lookup_with_a_url_reports_the_generation_as_completed()
    {
        // Mubert documents this endpoint as the way to "check if a track is created" and it spends no quota,
        // which is what makes a durable operation id worth reconciling during cancellation recovery. The
        // no-charge bit stays false because a completed track says nothing about refunds.
        var handler = new MubertHandler((_, _) => Json(
            HttpStatusCode.OK,
            "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3\"}]}}"));
        var provider = CreateProvider(handler);
        var result = await provider.ReconcileAcceptedSubmissionAsync("track-lookup-1");
        Assert.Equal(MusicSubmissionOutcome.Completed, result.Outcome);
        Assert.False(result.ProviderDeclaresNoCharge);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task Track_lookup_reporting_failure_does_not_claim_the_generation_was_free()
    {
        // The honest asymmetry the recovery ladder depends on: Mubert documents a failed track but offers no
        // "not charged on failed generations" statement, so a declared failure here must not be read as a
        // released refund.
        var handler = new MubertHandler((_, _) => Json(
            HttpStatusCode.OK,
            "{\"data\":{\"generations\":[{\"status\":\"failed\"}]}}"));
        var provider = CreateProvider(handler);
        var result = await provider.ReconcileAcceptedSubmissionAsync("track-lookup-2");
        Assert.Equal(MusicSubmissionOutcome.ProviderDeclaredFailure, result.Outcome);
        Assert.False(result.ProviderDeclaresNoCharge);
    }

    [Fact]
    public async Task Track_lookup_for_a_still_generating_track_reports_in_progress()
    {
        var handler = new MubertHandler((_, _) => Json(
            HttpStatusCode.OK,
            "{\"data\":{\"generations\":[{\"status\":\"waiting\"}]}}"));
        var provider = CreateProvider(handler);
        var result = await provider.ReconcileAcceptedSubmissionAsync("track-lookup-3");
        Assert.Equal(MusicSubmissionOutcome.InProgress, result.Outcome);
        Assert.False(result.ProviderDeclaresNoCharge);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":\"unknown track\"}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "upstream unavailable")]
    [InlineData(HttpStatusCode.OK, "{\"data\":{}}")]
    public async Task Track_lookup_that_cannot_answer_reports_unknown_instead_of_a_guess(HttpStatusCode status, string body)
    {
        // 404 (the track is not there for us to see), 503 (a transport/outage answer) and an empty track list
        // all mean the same thing to recovery: no evidence either way. Unknown keeps the fail-closed posture.
        var handler = new MubertHandler((_, _) => Json(status, body));
        var provider = CreateProvider(handler);
        var result = await provider.ReconcileAcceptedSubmissionAsync("track-lookup-404");
        Assert.Equal(MusicSubmissionOutcome.Unknown, result.Outcome);
        Assert.False(result.ProviderDeclaresNoCharge);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("taslim:dispatch-pending")]
    public async Task Sentinel_or_missing_operation_id_is_never_queried_at_the_provider(string operationId)
    {
        // The marker sentinel is our own label, not a vendor identifier: asking Mubert about it would be a
        // meaningless request (and on a paid lane any needless call is a cost risk). Recovery must read the
        // marker-only row as unconfirmed without approaching the transport.
        var handler = new MubertHandler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var provider = CreateProvider(handler);
        var result = await provider.ReconcileAcceptedSubmissionAsync(operationId);
        Assert.Equal(MusicSubmissionOutcome.Unknown, result.Outcome);
        Assert.Empty(handler.Requests);
    }
    private static MubertMusicGenerationProvider CreateProvider(
        HttpMessageHandler handler,
        string customerId = "customer",
        string accessToken = "token",
        int maxPollAttempts = 3,
        int pollIntervalMilliseconds = 1,
        int retryAttempts = 2,
        int retryDelayMilliseconds = 1,
        int maxOutputBytes = 1024,
        int providerTimeoutSeconds = 15,
        IProviderUrlPolicy? urlPolicy = null) => new(
            new HttpClient(handler),
            Options.Create(new MusicGenerationOptions
            {
                Enabled = true,
                ProviderKey = "mubert",
                Model = "mubert-text-to-music",
                ProviderTimeoutSeconds = providerTimeoutSeconds,
                MaxPromptCharacters = 255,
                MaxOutputBytes = maxOutputBytes,
                MubertApiBaseUrl = "https://music-api.mubert.com/api/v3/public/",
                MubertCustomerId = customerId,
                MubertAccessToken = accessToken,
                MubertMaxPollAttempts = maxPollAttempts,
                MubertPollIntervalMilliseconds = pollIntervalMilliseconds,
                MubertMaxRetryAttempts = retryAttempts,
                MubertRetryBaseDelayMilliseconds = retryDelayMilliseconds,
            }),
            NullLogger<MubertMusicGenerationProvider>.Instance,
            urlPolicy ?? new MockedProviderUrlPolicy());

    private static MusicGenerationInput Input() => new(
        "Warm piano and soft strings",
        "Background music for a product launch video",
        "cinematic",
        "calm",
        60,
        "instrumental",
        "en",
        "Launch bed",
        "Keep the arrangement gentle",
        null);

    private sealed class MubertHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> RequestBodies { get; } = [];
        private int requestCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var count = Interlocked.Increment(ref requestCount);
            return responder(request, count);
        }

        public async Task WaitForRequestAsync(int expected)
        {
            while (Requests.Count < expected) await Task.Delay(1);
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WaitAsync(cancellationToken));

        private static async Task<int> WaitAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class IoFaultingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("synthetic stream io failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("synthetic stream io failure");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Binary(HttpStatusCode status, string body, string contentType) => new(status)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) },
        },
    };

    private static MubertHandler CompleteTrackHandler(Func<int, HttpResponseMessage> download) => new(
        (_, requestNumber) => requestNumber switch
        {
            1 => Json(HttpStatusCode.OK, "{\"data\":{\"id\":\"track-fixture\",\"session_id\":\"session\"}}"),
            2 => Json(HttpStatusCode.OK, "{\"data\":{\"generations\":[{\"status\":\"completed\",\"url\":\"https://download.example/track.mp3\"}]}}"),
            _ => download(requestNumber),
        });

    private static HttpResponseMessage BinaryWithDeclaredLength(HttpStatusCode status, string body, string contentType, long length)
    {
        var response = Binary(status, body, contentType);
        response.Content.Headers.ContentLength = length;
        return response;
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location) => new(status)
    {
        Headers = { Location = new Uri(location) },
    };

    private sealed class MockedProviderUrlPolicy : IProviderUrlPolicy
    {
        public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException();
            return Task.CompletedTask;
        }
    }
}
