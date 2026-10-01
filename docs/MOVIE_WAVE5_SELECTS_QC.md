# Movie Wave 5: Selects QC Intelligence

## Scope

`movie-selects-qc.v1` is a provider-neutral, deterministic contract for turning bounded media-inspection evidence into reviewable segment-level select recommendations.

It supports issue dimensions for:

- face and identity;
- motion;
- visual artifacts;
- continuity;
- framing;
- text/signage when the shot explicitly declares that dimension relevant.

The contract describes **evidence and ranges**, not creative truth. It has no prompt, model, provider, credential, aesthetic score, or raw upstream payload field.

## Contract

The API-side seam is `IMovieSelectsQualityControl` and its default implementation is `MovieSelectsQualityControlService`.

A request contains:

- one or more named source segments;
- each segment's exact `[startSeconds, endSeconds)` source range;
- one check status per relevant dimension (`passed`, `issue`, `missing`, or `not_relevant`);
- zero or more bounded issues with a category, severity, stable code, and exact affected range;
- optional take/clip IDs for internal linkage only;
- optional requirements, including the relevant dimensions and minimum usable range duration.

A decision contains:

- the contract version;
- a recommendation (`recommend_selects`, `require_review`, or `no_usable_range`);
- `recommendedSelects`, each retaining its source segment ID and exact range;
- per-segment actions, usable ranges, issues, and stable reason codes;
- an explicit `requiresHumanReview` flag.

Recommended selects do not replace `MovieShot.SelectedTakeId`, `MovieShot.FinalTakeId`, or `MovieTake` approval. They are evidence-backed candidates for a later human or workflow command.

## Deterministic policy

1. A segment must have a positive, bounded range and a unique ID.
2. Relevant dimensions need explicit `passed` or `issue` evidence. Missing evidence cannot be treated as a pass.
3. Text/signage is not required by default; a shot opts into it by adding `text_signage` to its relevant categories.
4. An `issue` check must have at least one issue for the same category. A check/issue contradiction is invalid evidence.
5. Only explicitly reported `blocking` issue ranges are removed from a segment. Ranges before and after the issue are preserved when they meet the minimum usable duration.
6. `warning` issues remain visible and can retain a usable range, but they set `requiresHumanReview=true`.
7. A fully blocked segment produces `no_usable_range`; the evaluator does not infer a replacement range.
8. Issue ranges outside their declared segment are invalid evidence and require review; they are not silently clamped.
9. Segment and range order is stable. Re-evaluating the same request produces the same decision and JSON ordering.
10. No decision queues regeneration, invokes a provider, changes selection/finalization, creates a job, or charges usage.

This lets the production workflow salvage usable ranges before considering a missing insert or another explicit regeneration request. Any later regeneration must continue through the existing selective-regeneration, budget, accounting, provider-resilience, and recovery boundaries.

## Integration boundary

The contract is registered in the API container as a singleton because it is stateless and deterministic. It does not add a database table or migration. A future consumer may persist the decision alongside existing take/clip provenance or a review record, but should retain the original evidence, contract version, and source range identity.

A future media-inspection adapter must supply only provider-neutral measurements and stable evidence references. It remains responsible for making its own inspection output bounded and trustworthy. This evaluator does not claim that a face is the intended person, that a composition is artistically good, or that a segment should be published without the existing approval flow.

The current branch keeps all real movie/video/image/voice/music/SFX providers and customer charging unchanged and off by default.
