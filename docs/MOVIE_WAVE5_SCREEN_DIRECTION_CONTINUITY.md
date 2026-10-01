# Wave 5 Screen-Direction Continuity

Wave 5 adds a provider-neutral screen-direction continuity layer to Movie Studio. It makes spatial intent explicit before expensive generation and keeps review separate from take selection, regeneration, mastering, accounting, and timeline mutation.

## Contract

`MovieScreenDirectionPlan` is an optional, nullable plan on `MovieShot`:

- `axis`: bounded author-defined axis identifier, such as `corridor-a`;
- `orientation`: `side_a`, `side_b`, `neutral`, or `axis_break`;
- `entranceDirection` / `exitDirection`: `screen_left`, `screen_right`, `center`, `foreground`, `background`, `neutral`;
- `eyelineDirection`: the screen direction of the subject's look;
- `eyelineTarget`: bounded target label used to compare adjacent reverse-shot intent;
- `spatialAnchor`: bounded geography anchor such as a doorway, desk, or vehicle;
- `axisBreak`: explicit boolean that documents an intentional reset;
- `notes`: bounded director handoff notes;
- `schemaVersion`: currently `1`.

Legacy shots remain valid with a null plan. Invalid controlled direction values are rejected on shot create/update. The plan is persisted as bounded nullable `MovieShots.ScreenDirectionJson` and is exposed as the additive `screenDirection` property on shot responses.

## Review behavior

The existing read-only continuity routes now include screen-direction findings and safe recommendations:

- `GET /api/movie-studio/projects/{movieProjectId}/continuity-review`
- `GET /api/movie-studio/scenes/{sceneId}/continuity-review`
- `GET /api/movie-studio/shots/{shotId}/continuity-review`

Adjacent shots are evaluated in sequence order within each scene:

1. **Axis/orientation** — different axes or non-neutral orientations without an explicit reset produce a warning.
2. **Entrance/exit direction** — for a same-axis continuation, an exit on one screen side normally leads to an entrance on the opposite side.
3. **Eyelines** — adjacent shots aimed at the same target normally alternate screen direction for a shot/reverse-shot handoff.
4. **Shot-to-shot geography** — different spatial anchors without a declared transition produce a warning.

Findings reuse the existing `DirectorStoryFindingDto` contract with stable categories:

- `screen_direction_axis`
- `screen_direction_entrance_exit`
- `screen_direction_eyeline`
- `screen_direction_geography`

Each finding includes both shot-plan evidence and a bounded correction. Uncertainty is stated where the plan is only intent and cannot prove rendered camera position, blocking, or gaze.

The review response additionally carries `screenDirectionRecommendations`. Recommendations are explicitly `edit` or `insert` suggestions with target/reference shot IDs and `automaticRewrite: false`. An insert recommendation means “consider an approved neutral geography/bridging shot”; it does not create, reorder, regenerate, select, or delete anything.

## Safety and compatibility

- Review is read-only and remains workspace-scoped through the existing service and controller authorization paths.
- No generation job, provider adapter, cost estimate, customer charge, usage transaction, take selection, upscale, mastering, timeline, provenance, or recovery behavior is changed.
- Existing `Analyze(context)` callers continue to receive the original findings list; the additive `AnalyzeWithRecommendations(context)` supplies the extended result.
- Screen-direction data is provider/model/prompt neutral and is never sent to an external provider by this change.
- `20261001085812_AddMovieScreenDirectionContinuity` adds one nullable column only. No backfill is required; legacy rows remain null.

## Verification

Focused tests cover normalization/round-trip, all four adjacent-shot checks, safe edit/insert recommendation output, explicit axis-break suppression, invalid direction rejection, existing continuity review behavior, and workspace-scoped read-only API behavior.
