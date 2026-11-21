# Music Studio reference-selection eligibility API

Issue: **#708** acceptance criteria 3 and 4 — *"select an owned Asset … No UI faux upload: the
backend must associate authorized workspace Asset ID and server must validate access"*, and
*"rights to reference music must be confirmed … do not promise exact sound unless the provider
supports it"*.

This document describes the small authenticated, **read-only** API boundary that lets a user check an
already-stored private workspace audio or video Asset as a song reference **before** any paid
generation admission is reached.

## What this is, and what it is emphatically not

| It **does** | It **does not** |
| --- | --- |
| Confirm that an asset identifier resolves to an Asset the requester may actually read, inside the workspace they are asking about | Accept an upload, write bytes, create an Asset, or change any stored row |
| Report the server-recorded media facts for that asset (kind, MIME, container, size, recorded duration, whether the server recorded an audio track) | Probe, transcode, or extract anything: no `ffmpeg`, no byte reads, no new media pipeline |
| Return stable fail-closed reason codes for missing rights, ownership refusal, unsupported media and absent audio metadata | Grant permission to generate: nothing here admits a job, a provider call, a cost preflight, a usage row or a credit |
| Stay provider-free: it never names a provider and reports provider capability exactly as it stands (unverified today) | Enable Suno, sung lyrics, covers, or voice imitation of any real person |

A reference reported `isEligible: true` would mean only *"no server-recorded blocker at selection
time"*. On today's codebase that state is **unreachable by design**, because no reference-rights
attestation service exists and no provider's reference-conditioning contract has been verified; the
boundary therefore always answers with at least one blocker. That is the honest answer, not a bug to
work around: fabricating an allow would be exactly the "UI faux upload" failure mode #708 warns about.

## Endpoint

```http
GET /api/music-references/eligibility
    ?workspaceId={guid}
    &assetId={guid}[,{guid}…]
    &mediaType={audio|video}[,…]
    &intent={style-conditioning|melody-resemblance|authorized-cover|named-voice-imitation}
```

* `assetId` and `mediaType` are positional lists that must have the same length: each declared kind
  is matched to its identifier, and a misaligned list is refused with `400` rather than guessed.
* `mediaType` is a **client claim**. The server confirms it against the recorded Asset MIME type and
  the stored file's content type, and denies on any disagreement.
* `intent` defaults to `style-conditioning`. Naming a real singer is not accepted through this
  boundary at all (there is no right-to-use record service), so voice imitation cannot be smuggled in
  as a "milder" intent.
* Authentication is required (`401` otherwise). Workspace membership is checked before any asset
  lookup (`403` `MUSIC_REFERENCE_BOUNDARY_WORKSPACE_DENIED` otherwise), so a non-member of a shared
  workspace never learns whether the addressed identifiers resolve.
* Maximum ten identifiers per request. An oversized list is rejected at the transport edge with
  `400 MUSIC_REFERENCE_BOUNDARY_SELECTION_TOO_LARGE`, so no identifier in it is looked up at all;
  the authorizer repeats the same ceiling (`MUSIC_REFERENCE_AUTH_SELECTION_LIMIT`) as defense in
  depth for any other caller.

### Response

```json
{
  "isEligible": false,
  "workspaceId": "…",
  "verifiedFacts": [
    {
      "assetId": "…", "mediaType": "audio", "contentType": "audio/mpeg",
      "containerFormat": "mp3", "sizeBytes": 4000000,
      "durationSeconds": 42, "audioTrackDetected": true
    }
  ],
  "blockers": [
    { "code": "MUSIC_REFERENCE_RIGHTS_MISSING", "message": "…", "assetId": null }
  ]
}
```

`verifiedFacts` contains only fields the server itself recorded. `null` means *"never recorded"*,
never zero and never a guess — the duration of a reference video, for example, stays `null` until a
real inspection step records it. No storage key, original or stored file name, path, URL, provider
detail or content-derived text can appear in the payload.

## Fail-closed reason codes

Three namespaces stay deliberately separate, exactly as their owning slices specified, so a UI can
tell which layer refused:

**Boundary transport** (`apps/api/Controllers/MusicReferenceEligibilityController.cs`)

| Code | Meaning |
| --- | --- |
| `MUSIC_REFERENCE_BOUNDARY_SELECTION_MALFORMED` | Identifiers missing, unparseable, or not aligned with declared kinds |
| `MUSIC_REFERENCE_BOUNDARY_SELECTION_TOO_LARGE` | More identifiers than the per-request lookup ceiling |
| `MUSIC_REFERENCE_BOUNDARY_INTENT_UNSUPPORTED` | Reuse goal outside the four supported selection intents |
| `MUSIC_REFERENCE_BOUNDARY_WORKSPACE_DENIED` | Requester is not a member of the workspace in the request |

**Access layer** — `MUSIC_REFERENCE_AUTH_*` from `apps/api/Music/MusicReferenceAssetAuthorization.cs`
(PR #805): principal invalid, selections missing/invalid/duplicated/over ceiling, asset not
available, workspace mismatch, asset archived, file unavailable, file lineage mismatch, media kind
mismatch. Foreign and unknown identifiers collapse into the single
`MUSIC_REFERENCE_AUTH_ASSET_NOT_AVAILABLE` refusal so probing cannot distinguish them.

**Eligibility layer** — `MUSIC_REFERENCE_*` from
`apps/api/Music/MusicReferenceEligibilityPolicy.cs` (PR #776), including
`MUSIC_REFERENCE_RIGHTS_MISSING` / `MUSIC_REFERENCE_RIGHTS_NOT_VERIFIED` (rights),
`MUSIC_REFERENCE_CONTAINER_UNSUPPORTED` / `MUSIC_REFERENCE_MIME_UNSUPPORTED` (unsupported media),
`MUSIC_REFERENCE_DURATION_UNSUPPORTED` / `MUSIC_REFERENCE_AUDIO_TRACK_MISSING` (absent audio
metadata), and `MUSIC_REFERENCE_PROVIDER_CAPABILITY_MISSING` /
`MUSIC_REFERENCE_PROVIDER_*_UNSUPPORTED` (provider contract).

## How the evidence is joined

```
MusicStudioView / guided draft (assetId + mediaType claim)
        │
        ▼
MusicReferenceEligibilityController            ← authN + workspace fence, GET only
        │
        ▼
MusicReferenceEligibilityBoundary              ← this slice: the wiring, no new rules
        │            │
        │            ├─► MusicReferenceAssetAuthorizer (#805)  – resolves ids through the shared
        │            │     IAssetService visibility + membership rules; produces verified records
        │            │     from server rows only
        │            ▼
        │      MusicReferenceEligibilityPolicy (#776)          – pure fail-closed decision over
        │            ▲        evidence records it never produces itself
        │            │
        └────────────┘  maps authorized assets field-for-field onto
                        MusicReferenceAssetAccessEvidence + MusicReferenceMediaProbeEvidence;
                        rights and provider capability are passed as genuinely unverified
```

Before this slice, #805's producer was "wired to nothing" and #776's consumer had no producer, so a
reference asset identifier could travel from the browser toward the provider gate without any server
code confirming it was readable, in the right workspace, of the claimed kind, and actually an asset.
The boundary is that missing step, and it is registered in DI
(`apps/api/Program.cs`, two `AddScoped` lines) precisely so the route cannot be reached without
authentication.

## Consumption by the guided Music UI

The guided song-draft state (PR #810, `apps/web/src/lib/musicGuidedSongDraft.ts`) already carries
references as `{ mediaType: "audio" | "video", assetId: string }` pairs plus an explicit consent
token, and already reports `disabledFeatures` such as `referenceRestyle`, `referenceVideo` and
`voiceLikeness` with dotted-key `blockedReasons`. The mapping onto this endpoint is one-to-one:

| Draft state | Endpoint use |
| --- | --- |
| `references[]` from the audio picker (#745) and video picker (#751) | `assetId` / `mediaType` lists — the draft's privacy rule ("canonical GUIDs only") is the same rule the API enforces |
| `set_consent` token and `consentCapturedAt` | must **not** be sent as proof: this boundary records nothing and accepts no client checkbox; it returns `MUSIC_REFERENCE_RIGHTS_MISSING`, which is why the consent token is never a substitute for the future attestation step |
| `capabilities.referenceAudioSupported` / `referenceVideoSupported` | may be flipped from a provider-capability review only; a `200` with blockers here never enables them, and `MUSIC_REFERENCE_PROVIDER_CAPABILITY_MISSING` keeps them closed |
| `blockedReasons` / `disabledFeatures` | render `blockers[].code` verbatim as a localization key (`music.reference.blocker.<CODE>`), never parse `message`, keeping Sorani/Arabic/English rendering a renderer concern per #758/#796 |
| Reuse-goal selector (genre / instrumentation / tempo / mood / melody) | the `intent` parameter; `melody-resemblance` and `authorized-cover` stay disabled until a provider is verified for them |

Recommended UI flow: after a selection is made, call this endpoint once per draft change, render the
asset's `verifiedFacts` (duration, size, container) in the reference card so the user sees server
facts rather than a file name from their own machine, and keep the "generate" control disabled
whenever `isEligible` is false. Nothing in the response starts, prices or queues a generation.

## Test coverage

`apps/api.Tests/MusicReferenceEligibilityApiTests.cs` — thirteen provider-free HTTP tests through the
real pipeline (authentication, workspace fence, shared asset visibility rules, authorizer, policy):

* anonymous request is rejected; a non-member workspace is fenced before any lookup;
* an authorized audio reference reports its recorded facts and stays blocked on rights and provider
  capability, with no job, usage, credit or asset created by the check;
* archived assets, unrecorded container/duration (absent audio metadata), and a media-kind claim
  that contradicts the stored rows each deny with their own code and resolve to no verified facts;
* **privacy**: another account, and another workspace of the same member, resolve the asset to a
  uniform refusal that leaks no storage key, file name, asset name or even the addressed identifier;
  a shared project asset is resolvable by a second workspace member;
* a voice-imitation intent on an otherwise authorized reference is refused for a missing voice
  right-to-use record rather than silently downgraded to style conditioning;
* malformed selections and unsupported intents are refused as transport errors, an over-ceiling
  selection is refused before any identifier is looked up, and no provider, storage or billing
  token ever appears in a response body.

## Limits and next steps (not delivered here)

1. A reference-rights attestation step must store a record before any reference can become eligible;
   this boundary will report it through the policy's existing codes without new vocabulary.
2. Video references additionally need the inspection result (PR #774's extraction primitive is the
   owner of those facts); until then their duration and audio-track facts stay `null` and the policy
   denies with `MUSIC_REFERENCE_DURATION_UNSUPPORTED`.
3. The generation request path (#709) keeps its own fail-closed gate; this read-only boundary does
   not flip it, and no provider call, credential, cost cap or production setting changes here.
