using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

/// <summary>
/// Transport-log privacy for scholarly discovery (raised in the #1270 security review).
///
/// <c>HttpClientFactory</c> logs each request it processes at Information level, and the line it
/// writes contains the request target. For an ordinary API call that target is a path. For these
/// providers the target IS the query string: the reader's research topic in <c>q=</c>, and for
/// Google Books the deployment's configured key in <c>key=</c> as well. Whatever a deployment sets
/// its log level to, that line therefore carries a reader's private research interest and a
/// credential into whatever sink receives the logs — and a topic quoted in a log cannot be redacted
/// afterwards without losing the request altogether.
///
/// The feature registers a per-client transport-log filter, so the assertion has two halves: the
/// scholarly client leaks nothing, and an otherwise identical client WITHOUT that filter still
/// leaks, which is what makes the first half evidence rather than an accident of the harness.
/// </summary>
public sealed class AcademicSourceTransportLogPrivacyTests
{
    private const string SyntheticKey = "synthetic-books-key-4f9d2a7b";
    private const string SyntheticTerm = "vaccine-misinformation-policy-study";

    /// <summary>The name HttpClientFactory gives a typed client: its simple type name, not its full
    /// name (verified against the registered options). The feature's transport-log filter and the
    /// runtime's logging handler both derive from that one name, so this test has to use it exactly
    /// — otherwise it would be filtering, and asserting about, a client that does not exist.</summary>
    private static string ClientName<TClient>() => typeof(TClient).Name;

    private static string CapturedText(MusicStudioFailureLogCapturingLoggerProvider logs) =>
        string.Join("\n", logs.Records.Select(record =>
            $"{record.Category}|{record.Level}|{record.Message}|{record.StateValues}|{record.ExceptionText}"));

    [Fact]
    public async Task A_scholarly_provider_request_leaves_neither_the_key_nor_the_topic_in_the_logs()
    {
        var logs = new MusicStudioFailureLogCapturingLoggerProvider();
        using var handler = new ScriptedHandler((_, _) => AcademicFixture.Json("""{"totalItems":0}"""));
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerProvider>(logs);
        // Deliberately louder than any production default: if the request target is written at all,
        // a level this permissive will show it.
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AcademicSourceDiscoveryOptions.SectionName}:Enabled"] = "true",
            [$"{AcademicSourceDiscoveryOptions.SectionName}:GoogleBooksEnabled"] = "true",
            [$"{AcademicSourceDiscoveryOptions.SectionName}:GoogleBooksApiKey"] = SyntheticKey,
            [$"{AcademicSourceDiscoveryOptions.SectionName}:ContactMailto"] = "ops@example.com",
        }).Build();

        // The real composition root, so the client whose logging behaviour is under test is the one
        // the deployment builds. Only the transport underneath it is replaced — after the feature
        // registered its pinned sockets handler, and with no network involved.
        services.AddAcademicSourceDiscovery(configuration);
        services.Configure<HttpClientFactoryOptions>(ClientName<GoogleBooksAdapter>(), options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = handler));

        using var provider = services.BuildServiceProvider();
        var adapter = provider.GetRequiredService<GoogleBooksAdapter>();
        var found = await adapter.SearchWorksAsync(new AcademicDiscoveryQuery
        {
            SearchTerm = SyntheticTerm,
            MaxResults = 5,
        }, CancellationToken.None);

        // The request really went through the logging pipeline; an empty result is what the scripted
        // "totalItems": 0 answer should produce.
        Assert.Empty(found);
        Assert.Single(handler.Requests);
        Assert.Contains(SyntheticTerm, handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains(SyntheticKey, handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);

        var text = CapturedText(logs);
        Assert.DoesNotContain(SyntheticKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticTerm, text, StringComparison.Ordinal);
        Assert.DoesNotContain("key=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("q=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("www.googleapis.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_builtin_logging_pipeline_does_write_request_targets_when_unfiltered()
    {
        // Control for the test above. Same handler, same capture, same generous level — the only
        // difference is that this client is a plain named client with no scholarly filter on it. If
        // this stops failing, the previous test proves nothing, because the runtime would have gone
        // quiet for a different reason.
        var logs = new MusicStudioFailureLogCapturingLoggerProvider();
        using var handler = new ScriptedHandler((_, _) => AcademicFixture.Json("""{"totalItems":0}"""));
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerProvider>(logs);
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));
        const string controlName = "Taslim.Api.AcademicSources.TransportLogControl";
        services.AddHttpClient(controlName).ConfigurePrimaryHttpMessageHandler(() => handler);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(controlName);
        using var response = await client.GetAsync(
            $"https://www.googleapis.com/books/v1/volumes?q={SyntheticTerm}&key={SyntheticKey}");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains(SyntheticKey, CapturedText(logs), StringComparison.Ordinal);
    }
}
