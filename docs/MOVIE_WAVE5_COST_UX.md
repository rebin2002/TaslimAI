# Movie Wave 5 — Cost UX and Readiness

## Scope

The active Movie Production workspace now exposes a normal-user **Draft / Upgrade / Master** decision surface with readiness blockers and a planning comparison between an optimized workflow and a naive full-regeneration workflow.

The UX applies the filmmaking workflow rule that references and review decisions should be locked before expensive work:

1. start with a reversible Draft direction;
2. review usable ranges and keep the source plan intact;
3. retry only an affected shot when a pass needs attention;
4. carry selected material into Upgrade or Master instead of regenerating a whole scene.

## User contract

- **Optimized path** means draft-first, reference-locked, selective continuation.
- **Naive path** means escalating every shot or regenerating whole scenes before review.
- Estimates are shown only when the existing safe Movie overview cost read model has a known amount. Unknown is rendered as **Estimate pending**, never as zero.
- The comparison is a planning signal, not a price, reservation, entitlement, or customer charge.
- Draft, Upgrade, and Master are Taslim-owned finish labels. Provider, model, prompt, credential, and upstream response details remain outside the normal-user surface.
- Selecting a tier never queues work. Keyframe, motion, render/retry, and master hand-off actions now pass through an explicit in-product confirmation step.
- Readiness blockers distinguish missing shot plans, open review decisions, missing approved source frames, and missing selected takes for Master.

## Implementation

| Area | Change |
| --- | --- |
| `apps/web/src/lib/movieBudgetReadiness.ts` | Pure budget/readiness projection with unknown-cost preservation and deterministic optimized-versus-naive comparison when a safe amount exists |
| `apps/web/src/components/MovieBudgetReadinessPanel.tsx` | User-facing tier, estimate, blocker, warning, and planning-only UI |
| `apps/web/src/components/MovieProductionWorkspace.tsx` | Mounts the panel and gates expensive production actions with explicit confirmation |
| `apps/web/src/components/FullMovieWorkspaceView.tsx` | Loads the existing overview cost read model alongside the active Production module |
| `apps/web/src/app/globals.css` | Scoped responsive styles for the panel and confirmation surface |

No database schema or API migration is required. The change is additive to the existing read model and production adapter boundary; generation, accounting, provenance, selected-take-only mastering, recovery, and provider-disabled defaults are unchanged.

## Validation

- Frontend focused movie tests: **32 passed**.
- Frontend full suite: **160 passed**.
- TypeScript: **passed**.
- Production web build: **passed** with the repository-required HTTPS `NEXT_PUBLIC_API_URL` build variable.
- ESLint: **0 errors**, with 8 pre-existing unused-symbol warnings in `FullMovieWorkspaceView.tsx`.
- Backend tests: not runnable in the current sandbox because `dotnet` is not installed; no backend files were changed.
