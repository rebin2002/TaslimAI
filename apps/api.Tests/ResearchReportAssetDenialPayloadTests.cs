using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// One focused negative synthetic authorization probe for the creator-private
/// project-scoped Research report asset (#936, reviewed on PR #1042): what the
/// fail-closed denial response itself carries.
///
/// The sibling lanes pin the denial STATUS and content markers -
/// <c>ResearchSourceAssetRevocationTests</c> (#934/#1042) asserts 404 fail-closed
/// with no source markers echoed, and <c>ResearchProjectReportAssetComemberPrivacyTests</c>
/// (#1026) enumerates the non-creator co-member leak. Neither asserts the payload
/// MINIMALITY of the denial: that the error response returns no signed/download URL
/// of any form (no scheme, no storage key, no GUID identifiers) and no content
/// metadata (asset name, file name, MIME type, byte size, representation listing) -
/// only the repository's generic <c>ASSET_NOT_FOUND</c> error envelope.
///
/// This fact pins exactly that contract for the two subjects that must NEVER read
/// the report asset over the normal API:
///   1. a revoked workspace member (the #934 revoke axis on the research lane), and
///   2. a cross-workspace stranger who was never a member of the asset workspace.
/// Across all four asset read channels - metadata, DOCX download, inline read and
/// PDF representation download - every response must be the 404 JSON error envelope
/// with no URL-like content and none of the live asset's metadata values.
///
/// Review follow-up (independent review of <c>cf49b5e2</c>, 2026-11-18T23:10Z). The
/// first cut of this file proved "the body contains ASSET_NOT_FOUND" plus a finite
/// blacklist, which a response with extra fields or relative links could have
/// slipped through. Four arms were added so the contract is asserted positively and
/// not merely by absence:
///   A. EXACT ENVELOPE, BY ALLOW-LIST AND TYPE. The denial body must parse as one
///      JSON document whose root object has exactly the member <c>error</c>, whose
///      object has exactly the members <c>code</c> and <c>message</c>, both of type
///      string, with the generic code/copy. Any additional or case-variant key
///      (<c>downloadUrl</c>, <c>fields</c>, a top-level <c>assets</c> listing) is a
///      violation even when every blacklisted token is absent.
///   B. URL-BEARING RESPONSE HEADERS. A 404 denial must not carry a header whose
///      NAME is URL-bearing (<c>Location</c>, <c>Content-Location</c>, <c>Link</c>,
///      <c>Refresh</c>, <c>Content-Disposition</c>, any <c>*-Redirect</c> or any name
///      containing url/uri/link/location/redirect/href) and no header VALUE may echo
///      a live asset identifier. Name-based, so opaque base64 cookies are not false
///      positives while a relative link in <c>Link</c> is caught.
///   C. URL REFERENCES OF ANY RESOLUTION MODE IN THE PAYLOAD. Absolute
///      (<c>https://host/p</c>), scheme-/protocol-relative (<c>//host/p</c>) and
///      root-relative (<c>/api/assets/{id}/download</c>) references are each matched
///      explicitly in the body, so the "no signed/download URL of any form" claim is
///      no longer carried by a single <c>"://"</c> substring test.
///   D. LIVE MUTATION CONTROLS. The assertion core is a pure function over a
///      captured response, so the injected leaks the review asked for - a
///      <c>Location</c>/<c>Link</c> header and a relative asset URL in the body - are
///      replayed against it in-process and must be rejected for the documented arm,
///      while the pristine envelope must be accepted. Each arm therefore has a
///      demonstrated red state without touching production code.
///
/// The <c>Task.Delay(150)</c> the review flagged was removed: <c>GenerationJobsApiFactory</c>
/// creates the schema synchronously in its own constructor and xunit completes the
/// shared class fixture before constructing this class, so the extra <c>EnsureCreated</c>
/// here is idempotent and the delay guarded nothing.
///
/// Fixtures use the deterministic, provider-independent <see cref="ResearchApiFactory"/>
/// fakes: zero external provider calls, zero charge, synthetic <c>@example.com</c>
/// identities only, and no real user data. QA evidence only - not a security sign-off.
/// </summary>
public sealed class ResearchReportAssetDenialPayloadTests : IClassFixture<ResearchApiFactory>
{
    // Marker emitted by FakeResearchReportProvider (apps/api.Tests/ResearchGenerationTests.cs).
    private const string ReportTitle = "Solar energy research";

    // The generic denial envelope the repository is required to return, verbatim:
    // apps/api/Controllers/AssetsController.cs -> ApiResults.Error(404, "ASSET_NOT_FOUND", "Asset not found.")
    // -> apps/api/Infrastructure/ApiResults.cs -> ErrorEnvelope(ErrorBody(code, message, fields: null)),
    //    where fields is omitted by JsonIgnoreCondition.WhenWritingNull.
    private const string GenericErrorCode = "ASSET_NOT_FOUND";
    private const string GenericErrorMessage = "Asset not found.";
    private const string MinimalEnvelopeBody = "{\"error\":{\"code\":\"ASSET_NOT_FOUND\",\"message\":\"Asset not found.\"}}";

    private static readonly string[] AllowedRootEnvelopeKeys = { "error" };
    private static readonly string[] AllowedErrorBodyKeys = { "code", "message" };

    // Field names a content-metadata body would carry but an ASSET_NOT_FOUND
    // error envelope must never contain.
    private static readonly string[] ForbiddenMetadataFields =
    {
        "\"name\":", "\"mimeType\":", "\"sizeBytes\":", "\"fileName\":",
        "\"storageKey\":", "\"contentType\":", "\"representations\":",
        "\"downloadUrl\":", "\"url\":", "\"projectId\":", "\"workspaceId\":",
    };

    // Document media-type markers that must never name the withheld report bytes.
    private static readonly string[] ForbiddenDocumentMediaMarkers =
    {
        "wordprocessingml", "openxmlformats", "application/pdf",
    };

    // Header names that can hand a client a location to fetch from. Explicit list plus
    // a name-marker sweep so an ad-hoc header (X-Asset-Download-Url) is caught too;
    // matching is on the NAME only, so an opaque base64 cookie value is not a hit.
    private static readonly string[] UrlBearingHeaderNames =
    {
        "Location", "Content-Location", "Link", "Refresh", "Content-Disposition",
        "Content-Base", "Content-Space", "X-Accel-Redirect", "X-Redirect",
    };

    // Marker tokens, matched per hyphen-delimited header-name SEGMENT rather than as a
    // raw substring of the whole name: a substring sweep flags Content-Security-Policy
    // (base-UR"I"-ty) and every other honest header, which would make the arm fire on
    // correct responses and get it deleted. "X-Asset-Download-Url" still matches.
    private static readonly string[] UrlBearingHeaderNameTokens =
    {
        "url", "uri", "urn", "link", "location", "redirect", "href", "proxy",
    };

    // URL/URI references in any resolution mode. Each has its own pattern so a failure
    // names the resolution mode that leaked.
    private static readonly Regex AbsoluteUrlPattern = new(
        @"[A-Za-z][A-Za-z0-9+.\-]*://\S*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SchemeRelativeUrlPattern = new(
        @"(?<![A-Za-z0-9])//[^/\s""'<>]+/\S*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RootRelativeUrlPattern = new(
        @"(?<![A-Za-z0-9:])/(?!/)[A-Za-z0-9._~%\-@:+]+(?:/[A-Za-z0-9._~%\-@:+]*)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ResearchApiFactory factory;

    public ResearchReportAssetDenialPayloadTests(ResearchApiFactory factory)
    {
        this.factory = factory;
        // No artificial settle delay: the factory has already created the schema in its
        // own constructor and EnsureCreated is idempotent, so this only pins the scope.
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Revoked_member_and_cross_workspace_stranger_receive_no_url_or_metadata_on_research_report_asset_reads()
    {
        // Workspace A: owner + researcher member + shared project; researcher runs Research.
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Denial Payload Workspace Owner");
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id, "Denial Payload Program (synthetic)");

        using var researcher = factory.CreateClient();
        var researcherAuth = await Register(researcher, "Denial Payload Researcher");
        await SetMembership(ownerAuth.PersonalWorkspace.Id, researcherAuth.User.Id, attach: true);

        var created = await SendWithCsrf<CreateResearchGenerationResponse>(researcher, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            projectId = project.Id,
            question = "Summarize the synthetic evidence for the denial payload probe.",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        var job = await WaitForTerminal(researcher, created.Job.Id);
        Assert.True(job.Status == "Succeeded", $"Status={job.Status}; Code={job.ErrorCode}; Message={job.ErrorMessage}");

        // Live asset values that a leaky denial body could echo.
        var probe = ReadAssetProbe(created.Job.Id);
        var assetId = probe.Id;

        // Baseline while attached: the researcher (creator) reads metadata as 200 with the
        // very tokens the denial must withhold, so the forbidden-token set is discriminative
        // and the access path demonstrably worked before the denials below are asserted.
        var baseline = await researcher.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);
        var baselineBody = await baseline.Content.ReadAsStringAsync();
        Assert.Contains(probe.Name, baselineBody, StringComparison.Ordinal);
        Assert.Contains("\"mimeType\":", baselineBody, StringComparison.Ordinal);
        // The same arms, pointed at the creator's 200 metadata read, must flag it: the
        // probes are discriminative, and a live non-minimal payload is rejected in the
        // very shape the denials are required to avoid.
        Assert.NotEmpty(DenialPayloadViolations(Capture(baseline), "metadata", "creator baseline", probe));

        // Channel paths exercised for every denied subject.
        var channels = new (string Name, string Path)[]
        {
            ("metadata", $"/api/assets/{assetId}"),
            ("download", $"/api/assets/{assetId}/download"),
            ("inline download", $"/api/assets/{assetId}/download?inline=true"),
            ("pdf representation download", $"/api/assets/{assetId}/representations/{probe.PdfRepresentationId}/download"),
        };

        // Subject 1: cross-workspace stranger - its own workspace, never a member of A.
        using var stranger = factory.CreateClient();
        var strangerAuth = await Register(stranger, "Denial Payload Foreign Workspace User");
        Assert.NotEqual(strangerAuth.PersonalWorkspace.Id, ownerAuth.PersonalWorkspace.Id);
        foreach (var (name, path) in channels)
        {
            AssertMinimalDenial(await CaptureAsync(await stranger.GetAsync(path)), name, "cross-workspace stranger", probe);
        }

        // Subject 2: the researcher after membership revoke (#934 axis on the research lane).
        await SetMembership(ownerAuth.PersonalWorkspace.Id, researcherAuth.User.Id, attach: false);
        foreach (var (name, path) in channels)
        {
            AssertMinimalDenial(await CaptureAsync(await researcher.GetAsync(path)), name, "revoked member", probe);
        }
    }

    /// <summary>
    /// Mutation control, replayed in-process against the same assertion core the live
    /// probe uses: a denial that answers with a URL-bearing response header, or whose
    /// payload resolves to the private asset in any mode, must be rejected by the arm
    /// that documents it - and the pristine generic envelope must still be accepted, so
    /// the arms are live rather than vacuously failing.
    /// </summary>
    [Fact]
    public void Url_bearing_headers_and_relative_asset_urls_in_a_denial_are_rejected()
    {
        var probe = SyntheticProbe();

        // Positive control first: nothing below can fail because the core rejects
        // everything. The pristine generic denial, complete with an opaque base64
        // cookie whose value contains '/' characters, must be clean.
        AssertMinimalDenial(DenialResponse.Pristine(), "metadata", "pristine control", probe);

        var assetPath = "/api/assets/" + probe.Id + "/download";
        var mutants = new (string Name, DenialResponse Denial, string ExpectedTag)[]
        {
            ("Location: root-relative asset download",
                DenialResponse.Pristine().WithHeader("Location", assetPath), "[header:url-bearing]"),
            ("Link: scheme-relative enclosure",
                DenialResponse.Pristine().WithHeader("Link", "<//media.private.invalid/report.docx>; rel=\"enclosure\""), "[header:url-bearing]"),
            ("Content-Location: absolute signed URL",
                DenialResponse.Pristine().WithHeader("Content-Location", "https://cdn.private.invalid/signed?key=abc"), "[header:url-bearing]"),
            ("Content-Disposition: attachment with the owner's file name",
                DenialResponse.Pristine().AsDocumentLeak().WithHeader("Content-Disposition", "attachment; filename=\"" + probe.PdfFileName + "\""), "[header:url-bearing]"),
            ("Ad-hoc URL-bearing header name",
                DenialResponse.Pristine().WithHeader("X-Asset-Download-Url", assetPath), "[header:url-bearing]"),
            ("Header value echoing the live asset id",
                DenialResponse.Pristine().WithHeader("X-Asset-Id", probe.Id.ToString("D")), "[header:value-leak]"),
            ("Payload with a root-relative asset URL",
                DenialResponse.Pristine().WithBody(EnvelopeWithMessage("Asset not found. See " + assetPath)), "[payload:relative-url]"),
            ("Payload with a scheme-relative asset URL",
                DenialResponse.Pristine().WithBody(EnvelopeWithMessage("Asset not found. Mirror at //media.private.invalid/report.docx")), "[payload:relative-url]"),
            ("Payload with an absolute signed URL",
                DenialResponse.Pristine().WithBody(EnvelopeWithMessage("Asset not found. Fetch https://cdn.private.invalid/signed?key=abc")), "[payload:relative-url]"),
            ("Payload with an inline root-relative representation URL",
                DenialResponse.Pristine().WithBody(EnvelopeWithErrorMembers("\"representation\":\"/api/assets/" + probe.Id + "/representations/" + probe.PdfRepresentationId + "/download\"")), "[payload:relative-url]"),
        };

        foreach (var (name, denial, expectedTag) in mutants)
        {
            var violations = DenialPayloadViolations(denial, "metadata", "mutation control", probe);
            Assert.True(violations.Count > 0,
                $"Mutation control '{name}' was NOT rejected: the payload assertions are vacuous. Captured: {denial.Body}");
            Assert.True(violations.Any(item => item.StartsWith(expectedTag, StringComparison.Ordinal)),
                $"Mutation control '{name}' failed, but not for the documented arm {expectedTag}: {Join(violations)}");
        }

        // Resolution modes do not double-report: one scheme-relative and one absolute
        // leak each explain themselves exactly once, which keeps a red run readable and
        // proves the root-relative arm is not simply matching the tail of every URL.
        Assert.Equal(1, UrlViolationCount(DenialResponse.Pristine()
            .WithBody(EnvelopeWithMessage("Asset not found. Mirror at //media.private.invalid/report.docx")), probe));
        Assert.Equal(1, UrlViolationCount(DenialResponse.Pristine()
            .WithBody(EnvelopeWithMessage("Asset not found. Fetch https://cdn.private.invalid/signed?key=abc")), probe));
    }

    private static int UrlViolationCount(DenialResponse denial, AssetProbe probe) =>
        DenialPayloadViolations(denial, "metadata", "masking control", probe)
            .Count(item => item.StartsWith("[payload:relative-url]", StringComparison.Ordinal));

    /// <summary>
    /// Mutation control for the envelope allow-list: a response that keeps every
    /// blacklisted token out but adds an unlisted key, a wrong value type, a non-generic
    /// code or copy, or a non-JSON body, is no longer the exact generic error document
    /// and must be rejected.
    /// </summary>
    [Fact]
    public void A_denial_that_is_not_the_exact_generic_error_envelope_is_rejected()
    {
        var probe = SyntheticProbe();
        AssertMinimalDenial(DenialResponse.Pristine(), "metadata", "pristine control", probe);

        var mutants = new (string Name, DenialResponse Denial, string ExpectedTag)[]
        {
            ("Extra top-level listing key",
                DenialResponse.Pristine().WithBody("{\"error\":{\"code\":\"ASSET_NOT_FOUND\",\"message\":\"Asset not found.\"},\"assets\":[{\"id\":\"" + probe.Id + "\"}]}"), "[envelope:root-keys]"),
            ("Extra unlisted error member carrying a URL field name",
                DenialResponse.Pristine().WithBody(EnvelopeWithErrorMembers("\"downloadUrl\":\"/api/assets/" + probe.Id + "/download\"")), "[envelope:error-keys]"),
            ("Error body echoes the fields member",
                DenialResponse.Pristine().WithBody("{\"error\":{\"code\":\"ASSET_NOT_FOUND\",\"message\":\"Asset not found.\",\"fields\":{}}}"), "[envelope:error-keys]"),
            ("code is a number, not a string",
                DenialResponse.Pristine().WithBody("{\"error\":{\"code\":404,\"message\":\"Asset not found.\"}}"), "[envelope:type]"),
            ("error is a string, not an object",
                DenialResponse.Pristine().WithBody("{\"error\":\"ASSET_NOT_FOUND\"}"), "[envelope:type]"),
            ("Non-generic error code",
                DenialResponse.Pristine().WithBody(EnvelopeWithCode("ASSET_FILE_NOT_FOUND")), "[envelope:code]"),
            ("Generic code, non-generic copy",
                DenialResponse.Pristine().WithBody(EnvelopeWithMessage("Asset " + probe.Id.ToString("N") + " not found in this workspace.")), "[envelope:message]"),
            ("PascalCase envelope keys",
                DenialResponse.Pristine().WithBody("{\"Error\":{\"Code\":\"ASSET_NOT_FOUND\",\"Message\":\"Asset not found.\"}}"), "[envelope:root-keys]"),
            ("Payload is not JSON at all",
                DenialResponse.Pristine().WithBody("PK\u0003\u0004binary-docx-prefix-not-json"), "[envelope:parse]"),
            ("Payload is a JSON array",
                DenialResponse.Pristine().WithBody("[{\"code\":\"ASSET_NOT_FOUND\"}]"), "[envelope:type]"),
        };

        foreach (var (name, denial, expectedTag) in mutants)
        {
            var violations = DenialPayloadViolations(denial, "metadata", "mutation control", probe);
            Assert.True(violations.Count > 0,
                $"Envelope mutation control '{name}' was NOT rejected: the allow-list is not enforced. Captured: {denial.Body}");
            Assert.True(violations.Any(item => item.StartsWith(expectedTag, StringComparison.Ordinal)),
                $"Envelope mutation control '{name}' failed, but not for the documented arm {expectedTag}: {Join(violations)}");
        }
    }

    /// <summary>
    /// A fail-closed asset denial must be exactly the generic 404 ASSET_NOT_FOUND error
    /// envelope: JSON only, no URL-bearing header of any name, no document disposition,
    /// no resolvable URL in any resolution mode, none of the live asset's identifiers in
    /// any header or the body, and no content metadata values or field names.
    /// </summary>
    private static void AssertMinimalDenial(DenialResponse denial, string channel, string subject, AssetProbe probe)
    {
        var violations = DenialPayloadViolations(denial, channel, subject, probe);
        Assert.True(violations.Count == 0,
            $"{subject} on {channel} did not receive the minimal generic denial envelope ({violations.Count} violation(s)):\n - {Join(violations)}"
            + $"\n Captured: {(int)denial.StatusCode} {denial.ContentType}; headers [{Join(denial.Headers.Keys)}]; body {Truncate(denial.Body)}");
    }

    private static IReadOnlyList<string> DenialPayloadViolations(DenialResponse denial, string channel, string subject, AssetProbe probe)
    {
        var violations = new List<string>();

        if (denial.StatusCode != HttpStatusCode.NotFound)
        {
            violations.Add($"[status] {subject} on {channel} returned {(int)denial.StatusCode}, expected 404: {Truncate(denial.Body)}");
        }

        if (!denial.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add($"[content-type] {subject} on {channel} denial media type was '{denial.ContentType}', expected application/json");
        }

        foreach (var marker in ForbiddenDocumentMediaMarkers)
        {
            if (denial.ContentType.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"[content-type] media type '{denial.ContentType}' names the withheld document representation '{marker}'");
            }
        }

        // Arm B: no URL-bearing response header, and no header value echoing live asset data.
        foreach (var name in denial.Headers.Keys)
        {
            if (IsUrlBearingHeaderName(name))
            {
                violations.Add($"[header:url-bearing] {subject} on {channel} denial carried URL-bearing header '{name}' = '{Join(denial.Headers[name])}'");
            }
        }

        foreach (var (name, values) in denial.Headers)
        {
            foreach (var value in values)
            {
                foreach (var identifier in probe.LiveIdentifiers)
                {
                    if (value.Contains(identifier, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"[header:value-leak] {subject} on {channel} denial header '{name}' echoes live asset value '{identifier}'");
                    }
                }
            }
        }

        // Arm A: the exact generic envelope, by allow-listed keys and JSON types.
        CollectEnvelopeViolations(denial.Body, violations);

        // Arm C: no resolvable URL reference of any mode anywhere in the payload.
        CollectUrlViolations(denial.Body, violations);

        foreach (var identifier in probe.LiveIdentifiers)
        {
            if (denial.Body.Contains(identifier, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"[payload:identifier] {subject} on {channel} denial body echoes live asset value '{identifier}'");
            }
        }

        foreach (var value in probe.ContentMetadataValues.Concat(new[] { ReportTitle }))
        {
            if (denial.Body.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"[payload:metadata] {subject} on {channel} denial body echoes content-metadata value '{value}'");
            }
        }

        foreach (var field in ForbiddenMetadataFields)
        {
            if (denial.Body.Contains(field, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"[payload:metadata] {subject} on {channel} denial body carries content-metadata field name {field}");
            }
        }

        return violations;
    }

    private static void CollectEnvelopeViolations(string body, List<string> violations)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            violations.Add($"[envelope:parse] denial body is not exactly one JSON document ({exception.Message}): {Truncate(body)}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                violations.Add($"[envelope:type] denial JSON root is {root.ValueKind}, expected an object with exactly [{string.Join(", ", AllowedRootEnvelopeKeys)}]");
                return;
            }

            var rootKeys = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (!KeysMatch(rootKeys, AllowedRootEnvelopeKeys))
            {
                violations.Add($"[envelope:root-keys] denial JSON root keys [{string.Join(", ", rootKeys)}] are not the allow-listed generic envelope [{string.Join(", ", AllowedRootEnvelopeKeys)}]");
                return;
            }

            var error = root.GetProperty("error");
            if (error.ValueKind != JsonValueKind.Object)
            {
                violations.Add($"[envelope:type] denial envelope member 'error' is {error.ValueKind}, expected a string-keyed object");
                return;
            }

            var errorKeys = error.EnumerateObject().Select(property => property.Name).ToArray();
            if (!KeysMatch(errorKeys, AllowedErrorBodyKeys))
            {
                violations.Add($"[envelope:error-keys] denial error body keys [{string.Join(", ", errorKeys)}] are not the allow-listed generic set [{string.Join(", ", AllowedErrorBodyKeys)}]");
            }

            foreach (var required in AllowedErrorBodyKeys)
            {
                if (!error.TryGetProperty(required, out var member))
                {
                    violations.Add($"[envelope:type] denial error body is missing allow-listed member '{required}'");
                    continue;
                }

                if (member.ValueKind != JsonValueKind.String)
                {
                    violations.Add($"[envelope:type] denial error body member '{required}' is {member.ValueKind}, expected string");
                }
            }

            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && code.GetString() != GenericErrorCode)
            {
                violations.Add($"[envelope:code] denial error code was '{code.GetString()}', expected the generic '{GenericErrorCode}'");
            }

            if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                && message.GetString() != GenericErrorMessage)
            {
                violations.Add($"[envelope:message] denial error copy was '{message.GetString()}', expected the generic '{GenericErrorMessage}'");
            }
        }
    }

    private static void CollectUrlViolations(string body, List<string> violations)
    {
        // Scan in decreasing specificity and blank out what a more specific mode already
        // explained: "https://host/p" is an absolute URL, not also a root-relative one, so
        // a single leak is reported once, under the resolution mode that actually applies.
        var byAbsolute = Mask(AbsoluteUrlPattern, body);
        var bySchemeRelative = Mask(SchemeRelativeUrlPattern, byAbsolute.text);
        foreach (var (mode, pattern, text) in new[]
        {
            ("absolute", AbsoluteUrlPattern, body),
            ("scheme-relative", SchemeRelativeUrlPattern, byAbsolute.text),
            ("root-relative", RootRelativeUrlPattern, bySchemeRelative.text),
        })
        {
            var hits = pattern.Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (hits.Length > 0)
            {
                violations.Add($"[payload:relative-url] denial body contains {mode} URL reference(s) [{string.Join(", ", hits)}]: {Truncate(body)}");
            }
        }
    }

    /// <summary>Replaces every match with same-length NUL padding so positions survive.</summary>
    private static (string text, int count) Mask(Regex pattern, string value)
    {
        var count = 0;
        var masked = pattern.Replace(value, match =>
        {
            count += 1;
            return new string('\0', match.Value.Length);
        });
        return (masked, count);
    }

    private static bool KeysMatch(string[] actual, string[] allowed) =>
        actual.Length == allowed.Length
        && actual.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(allowed, StringComparer.Ordinal);

    private static bool IsUrlBearingHeaderName(string name) =>
        UrlBearingHeaderNames.Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
        || name.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(segment => UrlBearingHeaderNameTokens.Any(token => segment.Equals(token, StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(token, StringComparison.OrdinalIgnoreCase)));

    // Envelope builders for the mutation controls: the generic document, plus one
    // injected member. Deliberately plain concatenation so a mutant's shape is legible.
    private static string EnvelopeWithMessage(string message) =>
        "{\"error\":{\"code\":\"ASSET_NOT_FOUND\",\"message\":\"" + message + "\"}}";

    private static string EnvelopeWithCode(string code) =>
        "{\"error\":{\"code\":\"" + code + "\",\"message\":\"Asset not found.\"}}";

    private static string EnvelopeWithErrorMembers(string extraMemberJson) =>
        "{\"error\":{\"code\":\"ASSET_NOT_FOUND\",\"message\":\"Asset not found.\"," + extraMemberJson + "}}";

    /// <summary>
    /// A captured response reduced to the four facts the payload contract is about:
    /// status, media type, response header names/values, body text.
    /// </summary>
    private sealed record DenialResponse(
        HttpStatusCode StatusCode,
        string ContentType,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
        string Body)
    {
        public DenialResponse WithHeader(string name, string value) =>
            this with
            {
                Headers = new Dictionary<string, IReadOnlyList<string>>(Headers, StringComparer.OrdinalIgnoreCase)
                {
                    [name] = new[] { value },
                },
            };

        public DenialResponse WithBody(string body) => this with { Body = body };

        /// <summary>A denial that answers as the document itself instead of refusing.</summary>
        public DenialResponse AsDocumentLeak() => this with { StatusCode = HttpStatusCode.OK, ContentType = "application/pdf" };

        /// <summary>
        /// The minimal generic denial as the API returns it today, including an opaque
        /// base64 cookie value that contains '/' characters - a deliberate reminder that
        /// the URL arms look at the payload and at header NAMES, never at token bytes.
        /// </summary>
        public static DenialResponse Pristine() => new(
            HttpStatusCode.NotFound,
            "application/json",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = new[] { "application/json; charset=utf-8" },
                ["Cache-Control"] = new[] { "no-store, no-cache" },
                ["X-Content-Type-Options"] = new[] { "nosniff" },
                ["Set-Cookie"] = new[] { "taslim.csrf=CfDJ8Nr7VXf+2wqEjI4mYcPZ0dQlCqU1HKP8kZ3vVYQ9mE7tS2rJLbN5xT0pAqWf/RsZuGdXcVbNmKlJHgfDsaQ==; path=/; secure; httponly; samesite=strict" },
            },
            MinimalEnvelopeBody);
    }

    private static DenialResponse Capture(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers)
        {
            headers[name] = values.ToArray();
        }

        foreach (var (name, values) in response.Content.Headers)
        {
            headers[name] = values.ToArray();
        }

        return new DenialResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            headers,
            response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    private static async Task<DenialResponse> CaptureAsync(HttpResponseMessage response)
    {
        try
        {
            return Capture(response);
        }
        finally
        {
            response.Dispose();
        }
    }

    private sealed record AssetProbe(
        Guid Id,
        string Name,
        Guid PdfRepresentationId,
        string PdfFileName,
        string StorageKey,
        string StoredFileName,
        string OriginalFileName)
    {
        /// <summary>Opaque live identifiers - checked in headers as well as the body.</summary>
        public string[] LiveIdentifiers { get; init; } = Array.Empty<string>();

        /// <summary>Content-metadata values - checked in the body only.</summary>
        public string[] ContentMetadataValues { get; init; } = Array.Empty<string>();
    }

    private static AssetProbe SyntheticProbe() => CreateProbe(
        Guid.Parse("3f2a1c9d-7777-4bbb-9c8d-0a1b2c3d4e5f"),
        "Denial Control Report",
        Guid.Parse("9d8c7b6a-5544-4321-8765-4321fedcba98"),
        "denial-control-report.pdf",
        "private/9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c",
        "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6",
        "denial-control-report.docx");

    private static AssetProbe CreateProbe(Guid id, string name, Guid pdfRepresentationId, string pdfFileName, string storageKey, string storedFileName, string originalFileName)
    {
        var identifiers = new[]
        {
            id.ToString("D"), id.ToString("N"),
            pdfRepresentationId.ToString("D"), pdfRepresentationId.ToString("N"),
            storageKey, storedFileName,
        };
        var metadata = new[] { name, pdfFileName, originalFileName };
        return new AssetProbe(id, name, pdfRepresentationId, pdfFileName, storageKey, storedFileName, originalFileName)
        {
            LiveIdentifiers = identifiers,
            ContentMetadataValues = metadata,
        };
    }

    private AssetProbe ReadAssetProbe(Guid generationJobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = db.Assets.AsNoTracking()
            .Include(item => item.Representations)
            .Include(item => item.StoredFile)
            .Single(item => item.SourceGenerationJobId == generationJobId);
        Assert.Equal(AssetTypes.Research, asset.AssetType);
        var pdf = asset.Representations.Single(item => item.RepresentationType == AssetRepresentationTypes.Pdf);
        // The forbidden-token set must carry real discriminative values, never an empty
        // string that a containment test would treat as trivially leaked.
        Assert.False(string.IsNullOrWhiteSpace(asset.Name) || string.IsNullOrWhiteSpace(pdf.FileName)
            || string.IsNullOrWhiteSpace(asset.StoredFile!.StorageKey) || asset.StoredFile.StorageKey.Length <= 8
            || string.IsNullOrWhiteSpace(asset.StoredFile.StoredFileName) || asset.StoredFile.StoredFileName.Length <= 8);
        return CreateProbe(
            asset.Id,
            asset.Name,
            pdf.Id,
            pdf.FileName,
            asset.StoredFile!.StorageKey,
            asset.StoredFile.StoredFileName,
            asset.StoredFile.OriginalFileName);
    }

    private static string Join(IEnumerable<string> values) => string.Join(" | ", values);

    private static string Truncate(string value) => value.Length <= 400 ? value : value[..400];

    private async Task SetMembership(Guid workspaceId, Guid userId, bool attach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        if (attach)
        {
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = userId,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
        }
        else
        {
            var membership = await db.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == workspaceId && item.UserId == userId);
            db.WorkspaceMembers.Remove(membership);
        }
        await db.SaveChangesAsync();
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"rpdenial-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name) =>
        SendWithCsrf<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name, type = "General" });

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        GenerationJobDto? current = null;
        for (var attempt = 0; attempt < 320; attempt += 1)
        {
            using var response = await client.GetAsync($"/api/generation/jobs/{id}");
            if (response.IsSuccessStatusCode) current = await response.Content.ReadFromJsonAsync<GenerationJobDto>();
            if (current is not null && current.Status is "Succeeded" or "Failed" or "Cancelled") return current;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Research job {id} did not reach a terminal state. Last: {current?.Status ?? "null"}/{current?.ErrorCode}");
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        using var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} -> {(int)response.StatusCode}: {Truncate(await response.Content.ReadAsStringAsync())}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}
