# Movie Studio V2 Operational E2E Harness

This branch adds provider-safe operational coverage for the Movie V2 workflow without integrating Tasks 1–19.

## Harness surfaces

- `apps/api.Tests/MovieOperationalHarness.cs`
  - `MovieOperationalApiFactory` replaces only `IMovieVideoProvider` with the existing deterministic fake provider.
  - `MovieOperationalFixtures.CreateFullMovieAsync` creates a real Full Movie, V2 hierarchy, approved story, character, location, set, variation, prop, and world mappings.
  - `MovieOperationalE2ETests` covers the persisted production journey, role boundaries, Director approval boundary, reload reads, no-fake-output assertions, diagnostics, and Quick Movie regression.
  - The generated shot path uses the fake provider, persists a real `Asset`/`StoredFile` relation, and verifies that the clip is backed by that record.
  - Storyboard and keyframe candidates are intentionally persistence-only records. The test asserts that they have no `AssetId` or `GenerationJobId`.
- `apps/web/e2e/movie-v2-operational-fixtures.ts`
  - Reusable browser-side deterministic fixture seeding for branches that have the operational UI wired.
  - Uses authenticated API contracts, never fabricated UI state or provider-looking placeholder output.
  - Captures API response status diagnostics without wall-clock thresholds.
- `apps/web/e2e/movie-v2-operational.spec.ts`
  - Exercises the currently available Full Movie workspace surface, navigation, reload persistence, no-fake-output presentation, and Quick Movie separation.
  - Future Task 1–19 UI controls can call the same fixture contract when they are present on the integration branch.

## Focused validation

```bash
# API operational tests
DOTNET_ROOT=/tmp/dotnet PATH=/tmp/dotnet:$PATH \
  dotnet test apps/api.Tests/Taslim.Api.Tests.csproj \
  --filter 'FullyQualifiedName~MovieOperationalE2ETests'

# All Movie-focused API tests
DOTNET_ROOT=/tmp/dotnet PATH=/tmp/dotnet:$PATH \
  dotnet test apps/api.Tests/Taslim.Api.Tests.csproj \
  --filter 'FullyQualifiedName~Movie'

# Web unit/type/lint checks
cd apps/web
npm run test
npx tsc --noEmit -p tsconfig.json
npm run lint
npx playwright test e2e/movie-v2-operational.spec.ts --project=chromium --list

# Browser run when the API is available at E2E_API_URL/NEXT_PUBLIC_API_URL
E2E_API_URL=http://localhost:5000 \
  npx playwright test e2e/movie-v2-operational.spec.ts --project=chromium
```

## Explicit integration boundary

The API harness intentionally uses the production HTTP controllers, authorization, persistence, generation-job worker, output validation, Asset publication, and Director approval routes. It does not add production UI, bypass approval rules, call a paid provider, or merge any earlier task branch. The integration branch still needs to wire the completed Story, World, Storyboard, Keyframe, Production, Take, and Director controls to these persisted contracts and selectors.
