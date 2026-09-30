# Taslim AI — Movie Wave 4 Integration Conflict Map

**Fixed comparison base:** `origin/main` at `4142f533b4c2474586f18aee12025295c56a549d`

**Integration-prep branch:** `parallel/movie-wave4-integration-prep`

**Rule:** this branch starts from the verified `origin/main` tip, does not merge any other workstream branch, and adds only the provider-neutral export seam, the selected-status compatibility fix, focused deterministic acceptance, and integration documentation.

## Scope boundary

Wave 4 acceptance composes the existing Movie V2 production/take lifecycle with the existing voice/music generation jobs and a future-facing timeline/export hand-off. SFX, captions, timeline persistence, and an executable final export worker are **not present on the fixed base**. The acceptance harness represents those missing surfaces with deterministic test-only references and the migration-free `MovieWave4ExportSeam`; it does not fabricate production Assets, GenerationJobs, provider executions, or charges.

## Highest-risk hotspots

| Hotspot | Wave 4 collision | Required resolution |
|---|---|---|
| `apps/api/Movies/MovieEntities.cs` / `MovieV2Entities.cs` | Future audio, caption, timeline, and export relationships can create a second source-of-truth beside `MovieShot.SelectedTakeId` and `FinalTakeId`. | Keep the canonical selected/final pointers authoritative. A timeline must reference the selected final take explicitly; never infer selection from latest-created media. |
| `apps/api/Movies/MovieProduction.cs` / `MovieStudioService.cs` | Keyframe approval, render takes, and downstream audio/export may be advanced by unrelated status changes. | Preserve the existing stage workflow and stage provenance. Keyframe approval remains the gate for production work; export receives an approved keyframe reference. |
| `apps/api/Movies/MovieTakeUpscaleEligibility.cs` | The real lifecycle changes a take to `Selected`, while the original ready set only covered `Ready`, `ReviewRequired`, and `Approved`. | The prep branch adds `Selected` to the existing ready-status set. Candidate takes remain blocked by the selection pointer. |
| `apps/api/Movies/MovieFinalMastering.cs` | A future final-master output could be mistaken for an executable export or published Asset. | Keep mastering as an `AwaitingProvider`/`Blocked` hand-off until a future executor validates output, QC, provenance, and publication. |
| `apps/api/Movies/MovieWave4IntegrationSeam.cs` | Timeline/export contracts could leak provider/model/prompt metadata or become an implicit generation path. | Keep the seam provider-neutral, migration-free, deterministic, and side-effect free. It returns a hand-off decision and provenance hash only. |
| `apps/api/Generation/GenerationJobExecution.cs` / `UsageLedgerService.cs` | Audio or export retries could publish partial files or charge failed work. | Reuse prepare/validate/publish/finalize. On failure, discard prepared output, keep `UsageTransaction.Status=Failed`, and force `ChargedAmount=0`. Retry must preserve `RetryOfJobId`. |
| `apps/api/Program.cs` | Test adapters can accidentally replace disabled production registrations or enable a real audio/video path. | Fake adapters are registered only in `MovieWave4ApiFactory`; committed appsettings and production registration remain disabled for movie and audio providers. |
| `TaslimDbContext.cs` / model snapshot | Future timeline, captions, or export tables can collide with MovieAssembly and existing asset ownership. | No Wave 4 migration is added here. Before persistence lands, compose one final model, regenerate one snapshot, and validate fresh/upgrade PostgreSQL paths. |
| `apps/web/src/components/FullMovieWorkspaceView.tsx` | UI may render unpersisted tracks or expose internal provider metadata. | Future UI should consume product-safe timeline/export DTOs only, show hand-off state explicitly, and never render provider/model/prompt/credential fields. |

## Integration order

1. Start from `origin/main` at the fixed base SHA and run the Wave 3 regression gate.
2. Reuse the existing project, shot, production-version, approval, take, selection, finalization, and provenance contracts.
3. Verify selected-take-only upgrade against the server-owned selection pointer after the take is in `Selected` status.
4. Reuse existing voice/music generation job boundaries with test-host fake adapters. Keep SFX disabled until its canonical provider-neutral contract exists.
5. Validate the migration-free timeline seam: deterministic ordering, same-track overlap rejection, asset requirements, caption text, and stable provenance hash.
6. Validate the final export hand-off: approved keyframe, selected/final take equality, selected video source, complete tracks, and no side effects.
7. Exercise reload/provenance through the existing API read models.
8. Exercise failure, zero-charge accounting, no partial output, retry lineage, and successful retry recovery.
9. Only a later Wave 4 implementation branch may add persistent captions/timeline/export entities, and it must regenerate the composed EF snapshot rather than hand-editing it.

## Semantic conflicts to keep explicit

- **Selected take vs. latest take:** `SelectedTakeId` and `FinalTakeId` are the only authority. New takes never become export sources implicitly.
- **Upgrade vs. export:** the selected-take upgrade audit is a hand-off, not a completed upgrade and not an export source by itself.
- **Mastering vs. export:** a blocked or awaiting-provider master cannot be reported as a completed final export.
- **Audio success vs. timeline inclusion:** a voice/music job must have a published Asset before it can enter a non-caption timeline track.
- **SFX disabled vs. acceptance coverage:** the harness may use a deterministic non-persisted reference to test graph validation, but the product must not present it as a generated Asset.
- **Captions vs. media assets:** caption items require text and timing, not a fabricated audio/video Asset.
- **Estimate vs. charge:** provider-cost estimates and internal fake usage remain separate from customer charge; committed charging stays disabled.
- **Failure vs. retry:** a failed job has no billable Asset; retry creates a new job linked by `RetryOfJobId` and may publish only after successful output validation.
- **Normal-user safety:** timeline, export, and reload data contain product intent, IDs, statuses, and provenance only—not provider names, model names, prompts, or credentials.

## Migration collision order for a future Wave 4 persistence branch

1. Reconcile the final Movie V2 and Wave 3 model/snapshot first.
2. Decide ownership for audio track references, caption cues, timeline revisions, export requests, and final output Assets.
3. Reconcile delete behavior and uniqueness/idempotency constraints around project, shot, take, and export revision.
4. Generate one composed migration and snapshot.
5. Run EF pending-model checks, fresh PostgreSQL, base-to-upgrade PostgreSQL, migration-history, FK, index, and rollback/retry checks.
6. Add an executable export worker only after output validation, QC, Asset publication, and usage finalization are independently tested.

## Acceptance checklist

- [x] Fixed base SHA is verified as an ancestor of `origin/main`.
- [x] No other branch is merged into this prep branch.
- [x] Approved keyframe and stage provenance survive reload.
- [x] Multiple takes remain distinct and selection/finalization are explicit.
- [x] Selected take in canonical `Selected` status is eligible; alternate take is blocked.
- [x] Voice and music use fake test adapters only.
- [x] SFX/captions/timeline/export are represented as explicit migration-free seams, not fabricated product persistence.
- [x] Export hand-off creates no GenerationJob, MovieAssembly output, Asset, or charge.
- [x] Failed audio work has no partial output and `ChargedAmount=0`; retry lineage is preserved.
- [x] Production movie/audio providers and customer charging remain disabled in committed defaults.
- [x] No conflict markers, secrets, deployment, or paid/external generation activity.
