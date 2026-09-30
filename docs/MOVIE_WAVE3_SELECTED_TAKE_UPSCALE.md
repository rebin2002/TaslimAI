# Movie Wave 3: Selected-Take-Only Premium Mastering

## Rule

Drafts and alternative takes remain at their economical source resolution. Premium upscaling/mastering is eligible only after the user has explicitly selected the take for its canonical shot, or finalized it as the shot's final take. The service never scans candidates and never creates upscale work for every take.

## API boundary

- `GET /api/movie-studio/takes/{takeId}/upscale-eligibility`
- `POST /api/movie-studio/takes/{takeId}/upscale`

The POST endpoint records an eligibility decision and a pending hand-off audit row. It intentionally does **not** create a `GenerationJob`, invoke a media provider, or activate charging. A future executor must consume the pending audit row and re-check the current shot pointer before doing any premium work.

## Safeguards

1. The shot's server-side `SelectedTakeId` or `FinalTakeId` must point at the requested take.
2. The take must be in a ready state (`Ready`, `ReviewRequired`, or `Approved`). Draft, generating, failed, rejected, and archived takes are blocked.
3. The request requires the existing Movie Generate permission through the operational policy seam.
4. Repeated requests for the same take and master target are idempotent while a request is pending or completed.
5. Every request for an existing take is persisted as an append-only `MovieTakeUpscaleAudit`, including blocked attempts and the selection/finalization state observed at evaluation time.
6. DTOs contain only product-facing resolution and eligibility data; provider, model, prompt, credential, and raw execution details remain outside the normal-user contract.

## Wave-2 compatibility

The branch is based on `origin/main`. The Wave-2 adaptive-resolution integration branch is not an ancestor of `main`, so this change does not merge it or depend on its planning entities. `MovieUpscaleResolutionCatalog` uses the same provider-neutral master/source resolution vocabulary as a compatibility seam; a later integration can map the catalog to the canonical adaptive-resolution contract without changing the selected-take gate.

## Migration

`20260929213333_AddMovieTakeUpscaleAudit` adds the audit table and indexes. The migration is additive and does not change existing movie take selection semantics.
