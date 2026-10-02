# Movie Studio Reference Readiness

Wave5 adds a provider-neutral readiness evaluator for every movie shot. It assembles the existing production reference package with the shot plan and returns ten explainable dimensions:

- character
- wardrobe
- location / set
- lighting
- props
- spatial orientation
- camera plan
- dialogue
- continuity
- transition-out

## API

`GET /api/movie-studio/shots/{shotId}/reference-readiness` returns:

- `readinessPercentage` from complete dimensions divided by the ten-dimension checklist
- `status`: `ready`, `needs_review`, or `blocked`
- `readyForGeneration`: only false when a blocking item exists
- `items`, plus filtered `missing`, `blocked`, and `warnings` lists
- an evaluation hash and the latest matching override audit, when present

The evaluator uses only canonical Cast, World, Guide, shot, cinematography, continuity, and production reference-package data. It never exposes provider, model, prompt, credential, or paid-service fields.

## Blocking and overrides

Important execution references are blocking when absent or when the existing continuity package reports an authoritative conflict. Character identity, location / set identity, camera plan, and hard continuity conflicts cannot be bypassed. Optional or shot-specific gaps such as lighting specificity, spatial orientation on a supporting shot, or prop coverage may be explicitly overridden when the evaluator marks them safe.

A generation request may set `allowReferenceReadinessOverride: true` and provide a 10–2,000 character `referenceReadinessOverrideReason`. The production render, direct shot execution, and legacy shot generation paths validate this before cost estimation or job creation. The override is rejected if any blocker is unsafe or the reason is missing.

`POST /api/movie-studio/shots/{shotId}/reference-readiness/override` records an explicit, append-only audit decision. A matching evaluation hash activates that decision, so the unchanged shot reports `overridden` and can proceed; changing shot or package references invalidates it and requires a fresh decision. Generation paths also write the same audit at the execution boundary. The audit stores only the actor, source, bounded reason, bypassed item keys, readiness percentage, evaluation hash, and timestamp.

## Compatibility

The evaluator is additive. Existing shot planning and production contracts remain valid; new request fields default to `false` / `null`. The audit table is introduced by the `AddMovieReferenceReadinessOverrides` migration. No provider, billing, mastering, selected-take, provenance, or recovery behavior is replaced.
