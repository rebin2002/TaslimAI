# Movie Final Mastering Architecture

## Scope

Final mastering is a provider-neutral persistence and orchestration boundary for an approved, selected Movie V2 take. The first supported target is **UHD 4K (3840×2160)**, including 1080p source material. The boundary records intent and source/target metadata without claiming that pixels were produced.

## Request boundary

- `POST /api/movie-studio/takes/{takeId}/final-mastering`
- `GET /api/movie-studio/shots/{shotId}/final-mastering`

The request requires the existing Movie `FinalApproval` permission and an approved take that is selected or finalized for its shot. Repeating the same request for the same source take and target is idempotent. A replacement request may provide `supersedesMasterId`; the prior record becomes `Superseded` and the new record retains both sides of the replacement link.

## Persisted record

`MovieFinalMaster` tracks:

- source take, source clip, source Asset, and source dimensions when present;
- target profile and exact target dimensions;
- mastering state and an operator-safe state reason;
- QC status/result, output Asset, and Generation Job references (all remain empty until a real executor publishes a validated result);
- provider-neutral provenance JSON, requester, timestamps, and supersession links.

Source dimensions are read from bounded JSON metadata on the source Asset, StoredFile, clip, or take. A source without a video Asset or dimensions is recorded as `Blocked`; it is never reported as completed.

## No paid or fake execution

This branch does not register or call a mastering provider, create a Generation Job, charge usage, create an output Asset, or set `Completed`/`Passed` state. A source with usable metadata is recorded as `AwaitingProvider`, which is an explicit handoff state for a future executor. The existing movie provider and Generation Job abstractions remain unchanged.

A future executor must update the same record only after it has:

1. validated the source and target dimensions;
2. persisted a real output Asset through the existing Asset publication path;
3. run the existing media QC boundary and persisted the QC result; and
4. retained the source and provenance links.

## Wave-2 compatibility seam

No Wave-2 branch is merged. The implementation depends only on Movie V2 `MovieTake`, `MovieShot`, `Asset`, permission, and existing generation/QC contracts that are already on `main`. A future adaptive-resolution contract can add profiles or executor capabilities without changing the user-facing provider-neutral record or exposing provider/model/prompt details.
