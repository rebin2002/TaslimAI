# Movie Wave 4 — Dialogue Timing and Lip-Sync Planning

This slice adds a provider-neutral planning contract for dialogue timing and lip-sync eligibility. It is implemented in [`apps/api/Movies/MovieDialogueTimingContracts.cs`](../apps/api/Movies/MovieDialogueTimingContracts.cs) with focused coverage in [`apps/api.Tests/MovieDialogueTimingContractsTests.cs`](../apps/api.Tests/MovieDialogueTimingContractsTests.cs).

## Contract boundary

`MovieLipSyncPlanner` consumes server-resolved facts for:

- the canonical `MovieShot` selected-take pointer;
- the selected `MovieTake` status and published visual `Asset` reference;
- an approved, published audio `Asset` reference; and
- authored or already-measured voice and visual timing windows for each dialogue cue.

The planner does not read media bytes, infer phonemes, approve assets, create a `GenerationJob`, call a video/audio service, or change usage accounting. A future adapter can consume an eligible plan only after the existing execution and accounting gates are applied.

## Deterministic decisions

- **Blocked:** the shot pointer does not match the requested take, the take is not selected, the visual media is not a published video, the voice asset is not approved, or the voice media is not published audio.
- **Eligible:** the selected visual take and approved voice asset are ready, and all cue windows are within configured drift and duration tolerances.
- **Needs review:** identity and media gates pass, but timing drift, duration mismatch, or visual/voice language mismatch requires editorial review.

The plan includes contract version, opaque asset/take IDs, cue-level signed drift values, aggregate synchronization metadata, review state, and explicit escalation codes. It intentionally contains no provider, model, credential, prompt, price, or execution identifier.

## Migration and integration notes

No migration is required: this change is a pure planning contract and does not add a competing persistence hierarchy. It reuses the canonical `MovieShot.SelectedTakeId`/`MovieTake.Status` semantics and generic `Asset` boundary through server-resolved input references. Any persistence/API integration should add an additive route and durable review record only after the product decides where approved voice-asset state is owned.
