# The Last Seed — Wave 2 Production-Planning Acceptance

This acceptance path extends the existing deterministic `The Last Seed` Story/Guide journey without creating production fake creative output or calling a real video provider.

## Current base coverage

`apps/web/e2e/last-seed-acceptance.spec.ts` now verifies, in one authenticated Full Movie journey:

- the existing Story/Guide proposal review, approval, apply, reload, and evidence-backed consistency flow;
- the `propose_screenplay_scene` planning action remains available and produces a Last Seed-grounded screenplay scene;
- a 30-second scene planning fixture with bounded 8/10/12-second shots totaling 30 seconds;
- grounded shot purpose, subjects, location, continuity, production requirements, and cinematography fields;
- deterministic storyboard-candidate review and approval with no asset or generation-job output;
- world-continuity evidence for the Dry Village, Old Tree Field, and Last Seed records;
- reload persistence for the applied scene plan and approved storyboard record;
- provider-unavailable state, zero customer charge, no paid generation, and Quick Movie separation.

The reusable fixture is `apps/web/e2e/movie-wave2-last-seed-fixtures.ts`. It accepts a `prepareForProduction` callback so integration code can attach scene/shot continuity evidence before an approval snapshot is created.

## Later Wave 2 integration requirements

The supplied base commit does not yet expose the later optional Wave 2 shot production contract. The fixture therefore validates it when projected and adds a Playwright `integration-required` annotation when it is not present; it does not send unknown fields to a production endpoint or invent a fallback API.

The integrated shot response should expose a stable `productionContract` (or an explicitly mapped equivalent) with provider-neutral fields for:

- `durationSeconds` matching the persisted shot duration;
- `narrativeImportance` with a supported value, and the planting/turning-point shot able to carry `primary` or `critical` importance;
- `productionComplexity.level` from the supported complexity bands plus bounded drivers/notes;
- `qualityRequirements.minimumLevel` from the supported movie quality levels plus bounded acceptance criteria;
- `continuitySensitivity` and `upscaleSuitability` from their supported choices;
- `targetOutputRequirements.aspectRatio` matching the movie's `16:9` target;
- non-empty planning/production stage metadata after storyboard approval.

The existing route calls are the integration seam: scene creation, shot creation, scene shot-plan read, shot read, production-version creation/review, and world-continuity read. If a later branch introduces a dedicated scene-proposal endpoint, wire it inside the fixture rather than changing the Last Seed narrative constants or adding a test-only creative fallback.
