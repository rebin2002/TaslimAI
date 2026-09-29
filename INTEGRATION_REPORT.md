# TASLIM.AI — Movie Director Creative Intelligence Wave 1

## 1. Delivery scope

- **Integration branch:** `integration/movie-director-creative-intelligence-1`
- **Authoritative base:** `c4baee093b924d5fe6ec81208e8fc29db9d0ee53`
- **Final branch SHA:** the report commit is the final branch tip; the delivery message is authoritative for the exact SHA.
- **Main changed:** No
- **Production deployed:** No
- **Customer charging:** Remains disabled (`CustomerChargingEnabled: false`)
- **Movie/video provider:** Disabled (`MovieVideo:Enabled: false`); no provider call occurs in the Last Seed acceptance.

## 2. Approved SHA limitation

The repository currently exposes only these relevant approved Movie Director branch refs:

- `parallel/movie-ai-premise`
- `parallel/movie-ai-synopsis`
- `parallel/movie-ai-story-consistency`
- `parallel/movie-ai-output-validation`
- `parallel/movie-ai-director-routing`
- `parallel/movie-ai-story-writing-ux`

The approved Git objects for the remaining tasks were deleted upstream and are not present in the local object database, current GitHub refs, or authenticated GitHub API. No ancestry is invented. For every `UNAVAILABLE` row below, the exact statement is:

> Original approved Git object unavailable; final equivalent behavior verified.

## 3. Release-gate feature matrix

| Task | Final implementation file(s) | Final contract / service / function | Passing test / result | Original SHA ancestor |
|---|---|---|---|---|
| 01 — Real AI Story intelligence | `apps/api/Movies/MovieDirectorRouting.cs`, `MovieDirectorStory.cs`, `MovieDirectorStoryExecutor.cs` | `MovieDirectorStoryAiService`, `DirectorStoryAiDraft`, `DirectorStoryProposalPlanner.BuildFromAiDraft` | API Story/Director suite; exact Last Seed develops through the structured AI route; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 02 — Bounded grounded context | `apps/api/Movies/MovieDirectorServices.cs` | `MovieDirectorContextAssembler.AssembleStoryAsync`, bounded guide/story/Cast/World context, persisted Director context snapshot | API context-budget and Director tests; Last Seed asserts the exact brief and 30-second duration in `storyContext`; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 03 — Develop My Story | `apps/api/Movies/MovieDirectorStory.cs`, `apps/web/src/components/FullMovieWorkspaceView.tsx` | `develop_premise` action, empty Story default label **Develop my story**, review/apply workflow | `apps/web/e2e/last-seed-acceptance.spec.ts`; pending proposal, approval, explicit apply, and AI-suggested revision; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 04 — Premise intelligence | `apps/api/Movies/MovieDirectorStory.cs` | Grounded premise analysis and `AnalyzePremise` / `RenderPremise` path | `MovieDirectorPremisePlannerTests`; Last Seed asserts young farmer, dry village, grandfather, last seed, protection, rain, and hope; passed | **YES** — `f0a6d01e79ec1399bd3ff170f5740d37c02246c9` is an ancestor. |
| 05 — Logline intelligence | `apps/api/Movies/MovieDirectorStory.cs`, `MovieDirectorRouting.cs` | `ImproveLogline`, structured logline field, AI route and proposal change contract | API Story suite; Last Seed asserts story-specific logline and rejects generic placeholder prose; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 06 — Synopsis intelligence | `apps/api/Movies/MovieSynopsisDevelopment.cs`, `MovieDirectorStory.cs` | `AiMovieSynopsisDevelopmentService`, `MovieSynopsisDevelopmentDto`, short-form duration-aware proposal metadata | `MovieSynopsisDevelopmentTests`; Last Seed asserts 30 seconds, short scope, <=4 beats, <=1 complication, grounded synopsis, and proposed elements; passed | **YES** — `d7e6701e8e30c5a7af1481b8e7ed3d691852b1c5` is an ancestor. |
| 07 — Treatment intelligence | `apps/api/Movies/MovieDirectorStory.cs`, `MovieDirectorStoryExecutor.cs` | Structured treatment field, `CreateOrRefineTreatment`, duration-aware 30-second treatment | API Story suite; Last Seed asserts treatment contains the duration-aware 30-second arc; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 08 — Structured screenplay | `apps/api/Movies/MovieDirectorStory.cs`, `MovieStoryEntities.cs`, `MovieDirectorStoryExecutor.cs` | `MovieStorySceneRequest`, supported `Action`/`Dialogue` screenplay elements, scene apply executor | API Story suite; Last Seed asserts persisted Action and Dialogue elements plus Young Farmer and Grandfather cues; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 09 — Targeted rewrite / dialogue / pacing | `apps/api/Movies/MovieDirectorStory.cs`, `MovieDirectorStoryExecutor.cs` | Targeted scene/element IDs, `rewrite_selected_passage`, scoped editable revision apply | Last Seed captures all element contents and proves exactly one selected element changed; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 10 — Story consistency | `apps/api/Movies/MovieDirectorStoryConsistency.cs`, `MovieDirectorStory.cs`, `MovieDirectorStoryExecutor.cs` | `DirectorStoryConsistencyAnalyzer`, grounded evidence findings, review-only `identify_story_inconsistencies` | Last Seed asserts grounded findings contain evidence, `appliesToStory=false`, execution succeeds, and current revision ID is unchanged; passed | **YES** — `3367f5a5419eca7fc588a1bb834d4cf0711e9540` is an ancestor. |
| 11 — State-aware Story UX | `apps/web/src/components/FullMovieWorkspaceView.tsx` | Story state/provenance controls, revision history, review/apply focus behavior | `FullMovieWorkspaceView.test.tsx` plus full browser suite; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 12 — Movie Guide onboarding | `apps/api/Movies/MovieGuideService.cs`, `apps/web/src/components/FullMovieWorkspaceView.tsx` | Project-level Guide preparation and explicit lock | Last Seed creates a Full Movie, fills Guide fields, locks the Guide, and asserts locked revision; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 13 — Cast from Story | `apps/api/Movies/MovieStoryCast.cs`, `apps/api/Controllers/MovieStudioController.cs`, `apps/web/src/lib/api.ts` | Authenticated `GET /api/movie-studio/projects/{id}/cast/from-story`; `MovieStoryCastService.GetSuggestionsAsync` returns review-only suggestions with `isProposed` / `isEstablished` provenance | Last Seed identifies **YOUNG FARMER** and **GRANDFATHER**, marks both proposed/not established, and proves persisted Cast remains empty; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 14 — Room-aware Director | `apps/api/Movies/MovieDirectorServices.cs`, `apps/api/Movies/MovieDirectorStory.cs`, `apps/web/src/components/MovieDirectorPanel.tsx` | Project-scoped Story Director proposal path accepts Cast-room context without a shot; visible Cast-room control uses the Story proposal path | Last Seed enables the Cast-room **Create typed proposal** control without a shot, creates a pending proposal, rejects it, and also verifies the API boundary; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 15 — Placeholder audit | `apps/api/Ai/MockAiProvider.cs`, `apps/api/Movies/MovieDirectorCreativeOutputValidation.cs`, Movie Studio creation path | Development-only bounded Last Seed mock; production validator rejects generic/template prose; Full Movie starts empty | Last Seed asserts no fake scenes, shots, characters, locations, world records, clips, assemblies, or assets; full audit clean; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 16 — Creative output validation | `apps/api/Movies/MovieDirectorCreativeOutputValidation.cs` | Schema, grounding, quality, language, duplicate, target, and persistence-safety validation | API validation tests and Last Seed invalid/pending boundary; passed | **YES** — `d75661cb964c421a09b83168039f6a9994402fbf` is an ancestor. |
| 17 — Routing / cost / no deterministic creative fallback | `apps/api/Movies/MovieDirectorRouting.cs`, `MovieDirectorServices.cs`, `appsettings.json` | AI Core quality/cost route; provider/model metadata stays internal; unavailable/invalid creative output maps to safe errors; no production deterministic creative fallback | API routing tests; provider audit; Last Seed uses development `MockAiProvider` only and asserts video provider unavailable; passed | **YES** — repaired `425b832dd5b7a209637a8428da78d1afa4fc09c7` is an ancestor. |
| 18 — Story writing UX | `apps/web/src/components/FullMovieWorkspaceView.tsx`, `apps/web/src/lib/api.ts` | Dominant Story writing workspace, structured screenplay editor, provenance and revision history | 18 component tests in `FullMovieWorkspaceView.test.tsx`, 140 frontend tests total, and full browser suite; passed | **YES** — `f788a0d1265f6c3c29b34d09ae283f652a26aa85` is an ancestor. |
| 19 — Authorization / approval | `apps/api/Authorization/*`, `MovieDirectorServices.cs`, `MovieStoryCast.cs` | Workspace membership, Movie permissions, pending approval, explicit execution, immutable approved revision boundaries | 389 API tests, browser auth/workspace tests, and Last Seed cross-boundary checks; passed | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |
| 20 — Creative Intelligence E2E | `apps/web/e2e/last-seed-acceptance.spec.ts` | Exact authenticated Last Seed acceptance using the final integrated API, UI, database, and development mock path | **1 passed** dedicated test; included in **24 passed** complete Playwright suite | **UNAVAILABLE** — Original approved Git object unavailable; final equivalent behavior verified. |

## 4. Exact Last Seed acceptance

Implementation: `apps/web/e2e/last-seed-acceptance.spec.ts`

The test uses exactly:

- **Title:** `The Last Seed`
- **Brief:** the supplied near-future young farmer / dry village / grandfather / last seed brief
- **Settings:** 16:9, 30 seconds, Cinematic

The passing journey verifies:

1. Full Movie creation and empty initial production graph.
2. Project-level Guide entry and explicit lock.
3. Empty Story primary action is **Develop my story**.
4. Integrated Story Director proposal and structured mock response.
5. Review is required; approval alone does not apply.
6. Explicit execution creates an editable `AiSuggested` revision.
7. Grounded premise, non-placeholder logline, duration-aware treatment, short-form synopsis metadata, and structured Action/Dialogue screenplay.
8. Proposed Cast extraction for Young Farmer and Grandfather without inventing established personal names or persisting Cast cards.
9. Cast-room Director proposal without a shot, exercised through the visible Cast-room **Create typed proposal** control and the API boundary.
10. Selected-element rewrite changes only the selected target.
11. Evidence-backed consistency is review-only and does not change the revision.
12. Reload/persistence, provider-disabled status, Usage Ledger activity, zero customer charge, and Quick Movie separation.

## 5. Validation results

### Backend

- API build: **passed**, 0 warnings, 0 errors.
- Test project build: **passed**, 0 warnings, 0 errors.
- Complete API suite: **389 passed, 0 failed, 0 skipped**.
- The one transient operational test-host failure on the first run passed on the targeted rerun and the authoritative complete rerun.

### Frontend

- Vitest: **35 test files, 140 tests passed**.
- TypeScript: **passed** (`npx tsc --noEmit`).
- ESLint: **0 errors, 7 existing unused-variable warnings** in `FullMovieWorkspaceView.tsx`.
- Next production build: **passed** with `NEXT_PUBLIC_API_URL=https://api.example.test`.

### Browser

- Complete Playwright suite: **24 passed, 0 failed** (final rerun after the visible Cast-room Director branch).
- Dedicated exact Last Seed test: **1 passed**.
- Covered desktop Chromium, mobile Chromium at 390×844, RTL Chromium, accessibility, authentication/onboarding, Chat, generation safety, Movie Studio V2, navigation, workspace isolation, charging-disabled billing, Admin Operations denial, and the exact Last Seed journey.
- Only non-failing browser output is the existing Next.js smooth-scroll advisory.

### Database / migrations

- Previously validated real PostgreSQL migration inventory: **44 migrations**.
- Migration files changed in this repair: **none**.
- No new migration was expected or created because Cast-from-Story is a read-only projection and no schema/entity persistence was added.
- Previous fresh and upgrade PostgreSQL migration gates remain valid; they were not rerun because schema did not change.

## 6. Provider, fallback, charging, and authorization audit

- `MockAiProvider` changes are development-only structured responses for the exact acceptance; it never calls a remote AI vendor and reports zero/test usage.
- Production Story generation remains on the existing AI Core route; invalid or unavailable creative output is rejected safely.
- No deterministic production creative fallback was added. The only deterministic proposal wording is the existing review/planning boundary, not a production creative fallback.
- Movie/video provider remains disabled; no Runway or other movie provider call occurred.
- Customer charging remains off (`CustomerChargingEnabled: false`); the Last Seed test asserts `customerChargedAmount === 0`.
- Existing Usage Ledger path is used for Story AI usage accounting.
- Workspace and Movie permission checks remain enforced for Cast-from-Story, Story proposals, approval, apply, and execution.
- Approved Story revisions remain immutable; applied AI material is editable draft material until human approval.

## 7. Working tree and publication

- `git diff --check`: clean.
- Conflict-marker audit: clean.
- Migration/schema status: no migration files changed.
- `main` was not changed.
- Production was not deployed.
- The integration branch is pushed to the configured GitHub remote after the final report commit.
