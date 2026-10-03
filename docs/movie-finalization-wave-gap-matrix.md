# Movie Studio Finalization Wave — Gap Matrix

Audit of `rebin2002/TaslimAI` main at the start of the finalization wave.
Scope: gaps between the current platform and a production-ready end-to-end customer workflow.

Status legend: **FIXED** = corrected in this wave · **OPEN (code)** = pure-code gap still remaining ·
**OPEN (business/credential)** = cannot be closed by code alone.

## 0. What this wave shipped

The four Full Movie delivery rooms that previously rendered as "Soon" placeholders are now
operational customer surfaces backed by persisted records:

| Room | Module | Backed by |
|---|---|---|
| Audio | `MovieAudioWorkspace` | sound library, per-scene/shot sound tracks with review, soundtrack cues with versions and ducking intents, caption tracks with RTL flags and SRT/VTT export |
| QC | `MovieQualityWorkspace` | production checkpoint, continuity review findings, per-shot take evidence, and the deterministic quality gate recorded on every master |
| Exports | `MovieExportsWorkspace` | real final assembly requests, live progress, QC status, provenance, and an authenticated private master download |
| Team | `MovieTeamWorkspace` | server-authorized team roles, notes with resolution, review requests, and assignments |

Bounded take selects are now persisted and reviewed before they reach the timeline, and the movie
i18n namespace has full `en`/`ar`/`ku` parity.

## 1. Quick Movie and Full Movie entry

| Area | Status | Evidence |
|---|---|---|
| Quick Movie create → generate → result | OK | `MovieStudioView`, `MovieStudioOperations.QuickMovie` |
| Full Movie project + 14 module routes | OK | `FullMovieWorkspaceView.tsx` module slugs |
| Legacy scene/shot generation cost gate | **FIXED** | `MovieStudioService.QueueClipAsync` had no guardrail call |
| Direct production-shot execution cost gate | **FIXED** | `MovieShotExecutionService.QueueAsync` had no guardrail call |

## 2. Story / script / characters / locations / props

Story revisions, Director proposals, cast, world continuity, production kit revisions, character
production sheets, location geography sheets and the prop bible are all persisted and API-backed.
No code gap found in this area.

## 3. Storyboard, keyframes, shot generation, continuity

| Area | Status | Notes |
|---|---|---|
| Storyboard candidate review/approval | OK | `OperationalStoryboardModule` |
| Keyframe → motion → render → take | OK | `MovieProductionWorkspace`, `MovieProductionGenerationOrchestrator` |
| Character continuity snapshots | OK | `IMovieCharacterContinuityService` builds hashed/versioned snapshots |
| World continuity projection | OK | `MovieWorldContinuityProjector` |
| Screen direction in the shot snapshot | **FIXED** | `ShotSnapshot` carries the persisted screen-direction projection |
| First/last-frame + reference images to the provider | **FIXED** | `MovieVideoGenerationRequest` and the provider-neutral normalizer carry first/last/reference inputs |

## 4. Video generation and provider seams

| Area | Status | Notes |
|---|---|---|
| Runway provider seam | OK (seam) | `RunwayMovieVideoProvider` — activation needs a credential only |
| Manus provider seam | OK (seam) | `ManusMovieVideoProvider` — activation needs a credential only |
| Provider selection | **FIXED** | `IMovieVideoProviderRegistry` resolves the configured key and falls back unavailable |
| Direct video adapters | **OPEN (provider contract)** | the generic `IVideoGenerationAdapter` path is optional and separate from Movie's canonical provider path; no verified vendor contract is available for a Movie bridge |

## 5. Dialogue, voice, music, SFX, audio

| Area | Status | Notes |
|---|---|---|
| Dialogue lines, takes, timing, approval, selection | OK | `MovieDialogueVoice` |
| Dialogue take duration durability | **FIXED** | the provider result duration is persisted by `MovieDialogueVoiceExecutionStore.MarkReadyAsync` |
| Movie dialogue production provider | **FIXED (credential/config gate)** | `MovieDialogueVoiceProviderAdapter` reuses the existing generic `IVoiceGenerationProvider` result and usage contract; Movie and generic voice execution remain disabled by default |
| Movie SFX/ambience provider | **OPEN (provider contract)** | generic music generation is not a legitimate SFX/ambience contract; no provider-neutral SFX endpoint or output semantics are verified |
| Soundtrack cue/version/ducking metadata | OK | `MovieSoundtrack` |
| Soundtrack media submission | **OPEN (code/provider contract)** | `SubmitMediaAsync` now calls the seam, but the registered service remains unavailable because the seam has no durable generation-job, output publication, or provider-operation status contract |
| Soundtrack → final assembly audio mix | **FIXED** | approved cue assets can be projected into `AudioMixInputs` when requested |
| Captions | OK | authored/imported/exported, timeline-linked |

## 6. Timeline, editing, selects, QC, recovery

| Area | Status | Notes |
|---|---|---|
| Persisted timeline revisions/tracks/items + lock | OK | `MovieTimelineService` |
| Bounded take selects (create/list/review) | OK (API) | **OPEN (code):** no UI entry point |
| Bounded take selects (create/list/review) | **FIXED** | `MovieSelectsWorkspace` now persists the bounded range as a real select and requires reviewer approval before the timeline insert |
| Salvage recommendations + insert proposal | OK | `MovieSelectsWorkspace` |
| Full NLE editing surface | **OPEN (business)** | explicitly out of scope in the product copy |
| Persisted transition application | **OPEN (code)** | validator/contract only, no persisted transition edit API |
| QC service + reason codes | OK (domain) | **OPEN (code):** no QC controller or customer-facing QC room |
| QC service + reason codes | **FIXED** | `MovieQualityWorkspace` delivers the customer-facing review gate over checkpoints, continuity findings and assembly QC |
| Checkpoint/recovery | OK | `MovieProductionCheckpointService`, surfaced in the QC room |
| Selective regeneration | OK (API) | **OPEN (code):** not exposed in the UI |

## 7. Final assembly, export and download

| Area | Status | Notes |
|---|---|---|
| Assembly input validation and profile resolution | OK | `MovieFinalAssemblyService` |
| **Source materialization project scope** | **FIXED** | compared `Asset.ProjectId` against the MovieProject id instead of the root project id, so every source was rejected |
| **Assembly job input JSON contract** | **FIXED** | serialized with web/camelCase options while every worker reader used default options, so the job was rejected as `MOVIE_ASSEMBLY_TARGET_INVALID` before running |
| FFmpeg executor + deterministic output QC | OK | `FfmpegMovieFinalAssemblyExecutor` |
| Durable private master + provenance | OK | published through the normal asset publication path |
| **Authenticated master download** | **FIXED** | added `GET /api/movie-studio/final-assemblies/{id}/download` |
| Assembly duration measurement | **FIXED** | ffprobe-derived duration is persisted and used by assembly QC |

## 8. Usage, cost controls, security

| Area | Status | Notes |
|---|---|---|
| Provider-neutral cost estimator | OK | `MovieGenerationCostEstimation` |
| Budget director / preflight | OK | advisory planning only |
| Guardrail on the production orchestrator path | OK | estimate → guardrail → reject before queue |
| Guardrail on legacy scene/shot + direct execution paths | **FIXED** | now evaluated fail-closed before any job is queued |
| Charging default | OK (safe) | `Billing.CustomerChargingEnabled=false` |
| Auth, workspace/project authorization, CSRF | OK | all movie controllers `[Authorize]` + antiforgery middleware |
| DTO/log sanitization of provider details | OK | movie job DTOs suppress result/metadata and map errors to safe messages |
| Committed secrets | OK | none found in tracked files |

## 9. Mobile / RTL / localization

| Area | Status | Notes |
|---|---|---|
| Locale coverage for movie keys | **FIXED** | the 28 missing `movie.*` keys were translated; `en`/`ar`/`ku` now hold an identical key set |
| Full Movie workspace localization | **FIXED** | Movie localization keys and the delivery-room surfaces have `en`/`ar`/`ku` coverage |
| RTL layout | **FIXED (code)** | movie layout uses logical properties and locale direction; real-browser RTL validation remains an E2E gate |

## 10. Production end-to-end

The end-to-end customer path now reaches a durable, downloadable master:
project → story → cast/world → scenes/shots → storyboard → keyframes → shot generation →
take review/approval/selection → finalization → final assembly → private asset → download.

Remaining blockers to a paid production launch are all credential or business decisions:
provider credentials for movie video, dialogue voice and SFX; pricing/capability catalog review;
and approval to enable customer charging.
