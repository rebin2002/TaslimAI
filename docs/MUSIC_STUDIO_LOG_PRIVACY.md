# Music Studio lyrics and reference privacy in failure logs

Scope: the provider-free Music Studio paths that turn a rejected or failed
lyrics/reference draft into a **failure record** — an operational log line, a
persisted job failure, a usage record, or a client-visible issue payload. This
lane exists to keep user-authored lyric text and private reference material out
of those records. It is a verification lane: the product behaviour it pins is
described here, exercised deterministically in tests, and deliberately not
extended.

Out of scope by instruction: the `#708` preflight **UI** (open PR #772 editor,
open PRs #560/#561 studio wiring, `MusicLyricsPreflightPanel.tsx`) and the
**reference isolation worker** (open PRs #774, #805, #815 and the sibling
`Music Studio selected reference isolation` task). No file owned by those lanes
is read as a mutation target or changed here.

## What the guarantee is

1. **Server side.** `docs/MUSIC_STUDIO_ARCHITECTURE.md` states that upstream
   response bodies are never copied into job messages, usage metadata, or logs,
   and `docs/OPERATIONS_RUNBOOK.md` states that logs deliberately omit full
   prompts and private contents. For Music Studio that means a failure record
   carries identifiers (`JobId`, `WorkspaceId`, `RequestId`), a bounded failure
   code, a normalized category, an exception **type**, provider status when the
   adapter already exposes one safely, and elapsed time — never the `Description`,
   `Purpose`, `AdditionalInstructions`, `Title`, any lyric line, or a reference
   asset identifier.
2. **Client side.** `buildMusicLyricsDraft` returns an `issues` array as the
   *failure description* of a draft. It must stay a pure diagnostic: codes,
   ordinals and numeric limits. Anything a renderer prints, a test reporter
   serialises, or an error boundary forwards verbatim is exactly this array, so
   an issue that quotes user content is a leak channel.

## What was verified, and how

Verification is empirical, not by reading. `MusicStudioFailureLogPrivacyTests`
runs the real request pipeline against a locally injected deterministic
provider and a capturing `ILoggerProvider`, with reserved markers standing in
for lyric lines, description text, additional instructions, a title, and a
reference asset id:

| Scenario | Why it is the interesting one | Result |
| --- | --- | --- |
| `song_with_vocals` with lyric markers, admitted with the deterministic `test` provider key | The capability catalog returns `SupportsLyricsToVocals: false` for every key, so this fails closed **before** cost preflight, job creation, usage persistence and any provider call. | `400`, no job row, no usage row, **0 marker hits in the captured log** |
| A `lyrics` field of the wrong JSON type | Model binding rejects it, so the rejected *value* is the thing at risk of being echoed. | `400`, **0 marker hits** |
| Lyric text persisted through the generic job endpoint, then the job fails | This is the honest stress case: the lyric text genuinely exists in `GenerationJob.InputJson` and the job reaches `Failed`. | `Failed`; marker present in `InputJson` exactly once and absent from `ErrorMessage`, `UsageTransaction.SafeMetadataJson`/`FailureCode` and `GenerationProviderAttempt.SafeMetadataJson`/`FailureCode`; **0 marker hits** |
| A provider adapter that quotes the caller's own words into its own exception text | The worst realistic upstream behaviour: the words are inside `Exception.Message`, which every `Log*(exception, ...)` site renders. | `Failed`; the worker's `Error` record is present and carries `ExceptionType` only — the quoted text never reaches the log |
| The same admission rejection with synthetic Arabic (tashkeel-bearing) and Sorani lyric lines under `ar`/`iraqi_baghdadi` | Arabic and Sorani are the product's two RTL lyric languages (#708) and the realistic private content of its drafts; an echo risk that only reproduces in non-Latin script is still an echo risk. | `400`; the envelope contains no Arabic-script codepoint in any range the two scripts use, **0 marker hits** |
| Arabic/Sorani lyric text persisted through the generic job endpoint, then the job fails | RTL mirror of the honest stress case: the script text genuinely exists in `InputJson`. | `Failed`; markers present in `InputJson`, absent from `ErrorMessage` (which is additionally swept for any Arabic-script codepoint), usage/attempt metadata and **all captured logs** |

Each assertion is guarded against a silent pass: the test fails if the scenario
produced no log records at all, and separately requires at least one
`Taslim.Api*` category record, so an unwired capture cannot masquerade as
evidence.

A broad static sweep of every `ILogger` call site in `apps/api` accompanied this
(no lyric-, reference-, `InputJson`- or prompt-bearing template or argument was
found), as did inspection of `MusicGenerationRequestValidator`,
`MusicGenerationContractMapper`, `MusicGenerationJobHandler`,
`GenerationJobErrorMessages`, `GenerationJobContractMapper.ToDto` and the global
exception handler.

## The defect this lane fixes

The client half was not clean. `musicLyricsDraftPayload.ts` documented that the
draft-local section `id` "never leaves the payload" and that issues contain
"never ... lyric text, dialect copy or reference file names", but `mapSections`
copied `entry.id` **verbatim, unbounded and unfiltered**, into
`issues[].sectionId` for `section_type_invalid`, `section_empty`,
`lines_too_many` and `line_too_long`.

That is a content channel, not a naming convenience, because the assumption the
code makes is false in this repository: the rest of the Music Studio family
builds identifiers straight out of private user content.
`localReferenceAudioId` is `local:<size>:<file name>`,
`localReferenceVideoId` is `local-video:<size>:<file name>`, and the guided
handoff in `musicGuidedSongDraft.ts` copies a caller's `id` through with only a
`trim()`. Any editor row keyed from a section heading, a first line, or a picked
file name therefore had its text reproduced inside the failure payload, and the
preflight panel renders `issue.sectionId` into visible text.

The tell is the asymmetry inside the same module: the **reference** channel was
already hardened. `mapReferences` validates `assetId` against a canonical GUID
pattern and, when it fails, reports `reason: "asset_id"` — it deliberately
refuses to echo the offending value, which is precisely how a private URL or
file name is kept out of the diagnostic. The lyrics channel never got the
equivalent treatment.

The fix adds `safeDraftSectionId` with
`MUSIC_LYRICS_MAX_SECTION_ID_CHARACTERS = 64` and routes **every `sectionId`
emission this mapper makes** through it. A `sectionId` survives only as an opaque ASCII token
(`[A-Za-z0-9][A-Za-z0-9_-]{0,63}` after trimming); everything else is **dropped,
never truncated**, because a truncated echo still publishes a prefix of the
user's words. `sectionOrdinal` is always present, so dropping an unsafe
identifier costs the row's name and never the problem's addressability. Opaque
tokens the editors already generate (`s1`, `a`, `hook1`, `blank`, `section-3`,
`row_7`) keep working, which is asserted positively as well as negatively.

`safeDraftSectionId` applies `String.prototype.trim()`, which also strips
`U+00A0` (no-break space) and the other White_Space codepoints. A
whitespace-padded token therefore **normalises** rather than drops
(`"\u00A0s1\u00A0"` maps to `s1`). That is intended: padding is not content,
and the positive controls above pin it so a future reader does not "fix" the
normaliser into a dropper.

## Scope of the guarantee: one emission channel is outside this lane

The rule above binds the four codes this mapper emits
(`section_type_invalid`, `section_empty`, `lines_too_many`, `line_too_long`).
It does **not** bind a caller that appends its own issue records, and one such
caller exists on `main`.

`MusicLyricsPreflightPanel.tsx` adds a gate issue of its own,
`musicLyricsPreflightGateIssueCode = "lyrics_invisible_only"`, whose
`sectionId` is copied raw at `:240`
(`const sectionId = typeof entry?.id === "string" ? entry.id : undefined`),
placed on the gate record at `:259` and merged with the mapper's array at
`:265-274` without re-filtering. The module header of the panel never calls
`safeDraftSectionId` (0 references on current `main`).

Reproduced against this head through the panel's real exported
`evaluateMusicLyricsPreflight`, with one invisible-only synthetic section
(`\u200B\u200C\uFEFF \u2060\u180E`, i.e. `musicLyricsVisibleGlyphCount === 0` —
exactly the case the gate exists to catch) and synthetic identifiers only:

| Raw `entry.id` | `issues[]` `sectionId` on the returned array |
| --- | --- |
| `local:4096:گۆرانی_سۆران_کۆتایی.wav` | echoed verbatim (Sorani file name) |
| `https://cdn.invalid/signed/PRIVATE-TOKEN?t=1` | echoed verbatim (signed URL) |
| `يا قلبُ الوطنِ` | echoed verbatim (Arabic lyric line) |
| `ڕەنگێکی_نهێنی` | echoed verbatim (Sorani letters) |
| `s1` | echoed — correct, it is an opaque token |

Severity, stated precisely: the panel renders this code as the localized
`invisibleOnlyIssue` string (`:462-464`), so the raw identifier does **not**
reach visible DOM text for the gate code, and the content is a local-draft
identifier echoed back into the same user's own session — not remote
disclosure. What fails is the **durability** clause: a returned array that is
not uniformly safe cannot be handed to an error reporter on the strength of
this document, and `sectionId` is a field on the shared issue type, so any
future gate inherits the same obligation.

This repo's suites are green and blind to it: `musicLyricsDraftPayload.test.ts`
32/32, `MusicLyricsPreflightPanel.test.tsx` 25/25 and
`MusicLyricsPreflightPanel.accessibility.test.tsx` 10/10 pass together,
because every panel fixture uses opaque ids and no test drives the gate with a
content-bearing one. The new server-side C# lock cannot see this channel.

**Ownership, respected rather than taken:** the panel and its a11y test are
claimed by open PR **#847** (head `a8d39947`, which modifies both), and the
editor/preview UI by **#772** and merged **#939**. This lane therefore records
the gap and does not edit a fifth path. The fix belongs to that lane: route the
gate's `sectionId` through this module's exported `safeDraftSectionId` (it is
already exported for exactly this reason) and pin it with a test that drives
the gate with a content-bearing identifier. Until then, read the client
guarantee as "every `sectionId` this mapper emits", which is what the header
now says.

## Arabic/Sorani synthetic coverage

The privacy claim is script-independent, but the corpus proving it was not:
the first cut of the tests exercised the identifier filter with Latin tokens
plus one incidental Sorani line. This batch adds a dedicated Arabic/Sorani
synthetic corpus on both sides of the boundary, all of it test copy marked
`[TEST ONLY]` that quotes no user data:

- Client (`musicLyricsDraftPayload.test.ts`): Arabic lyric lines, Sorani
  identifiers using the script's own letters (`ڕ ڵ ۆ ێ ە چ پ ژ گ`), bidi
  embeds/overrides (`U+202B`, `U+202E`, `U+200F`), and `local:<size>:…mp3`
  identifiers shaped from Arabic and Sorani file names are all dropped from
  rejection issues; dialect copy in Arabic script (`عِرَاقِي`) is never
  echoed; a structural sweep asserts **every** issue record serialises to pure
  ASCII across an Arabic/Sorani violation battery. The positive controls prove
  the filter guards identifiers only: a clean Arabic (`ar` + `iraqi_baghdadi`)
  and a clean Sorani (`ckb`) draft still map with lyric lines byte-verbatim.
- Server (`MusicStudioFailureLogPrivacyTests.cs`): the two table rows above,
  plus the two RTL markers folded into the shared `CarriesMarker` sweep, so
  every pre-existing log assertion now also fails on an Arabic/Sorani leak.

## Ownership boundaries respected

Audited file-by-file against all 516 open PRs on `main` before this branch was
cut. This slice touches exactly four paths, none claimed by any open PR:

- `apps/web/src/lib/musicLyricsDraftPayload.ts` (fix + corrected header claim)
- `apps/web/src/lib/musicLyricsDraftPayload.test.ts` (reproduction + regression)
- `apps/api.Tests/MusicStudioFailureLogPrivacyTests.cs` (new provider-free lock)
- `docs/MUSIC_STUDIO_LOG_PRIVACY.md` (this file)

Deliberately untouched, with their owners:

| Path | Owner | Why untouched |
| --- | --- | --- |
| `MusicGenerationContracts.cs`, `MusicGenerationRequestValidator.cs`, `MusicGenerationCapabilityCatalog.cs`, `MusicGenerationSongContractTests.cs`, `MusicInvisibleTextAdmissionTests.cs` | open PR #709 (with #812 as co-claimant) | Contract and validator authority stays there; this lane only observes their fail-closed behaviour. |
| `MusicGenerationJobHandler.cs`, `MusicOutputInspectorTests.cs` | open PRs #547/#551 | Handler wiring is that lane's deliverable. |
| `MubertMusicGenerationProvider.cs`, `StableAudioMusicGenerationProvider.cs` | open PRs #508/#511 | Adapter-level redaction belongs to the provider lane; the log lock here is adapter-independent. |
| `MusicGenerationController.cs`, `MusicGenerationAuthorizationTests.cs` | open PR #359 | Controller surface owned there. |
| `GenerationJobExecution.cs` | open PRs #353/#361 (and others) | Worker failure-log shape is heavily claimed; this lane pins it instead of editing it. |
| `OperationalObservability.cs`, `OperationalFailureClassification.cs` (+ tests) | open PRs #258, #515/#533 | Redaction infrastructure owned there. |
| `MusicLyricsPreflightPanel.tsx` (+ test and a11y test), `LyricsSectionEditor.tsx` | `#708` UI: open PRs #772 and #847 (panel + a11y test), merged #939 | Explicitly excluded by the task; the panel keeps its own local mapping, including the unfiltered gate `sectionId` recorded above. |
| `musicStudioState.ts` (+ test) | open PRs #572/#573/#847 | Studio job state owned there. |
| `MusicReferenceEligibility*`, `MusicReferenceAssetAuthorization*`, `MusicReferenceVideoAudioExtraction*`, `MusicReferenceAudioValidationRegressionTests.cs` | open PRs #730/#773/#774/#776/#805/#815 and the reference isolation task | The reference isolation worker is explicitly excluded. |
| `musicGuidedSongDraft.ts`, `musicReferenceAudioState.ts`, `musicReferenceVideoState.ts` | unclaimed | Read as evidence for how identifiers are formed; unchanged, because the correct boundary is where the failure payload is built. |

## Guardrails honoured

No production secret or customer data was read, moved, or emitted. No charging
path, cost gate, or paid provider was enabled or called: `MusicGeneration:Enabled`
remains `false` in `appsettings.json` and `appsettings.Production.json`, every
scenario uses the injected deterministic local provider, and sung lyrics still
fail closed for every provider key. Nothing was merged, deployed or force
pushed, and independent security review is not bypassed — this branch is a PR
for review like any other.

## Checks this slice is expected to pass

All seven hosted checks on a PR based at `main`: Deployment smoke helper
contracts, Frontend checks, API integration tests, Browser E2E, Autopilot
next-wave gate, Production container smoke, and Release evidence and safety
contract. Targeted local equivalents: the mapper test file, the full web unit
suite, `tsc --noEmit`, `eslint`, and the new C# test class.
