# Movie Wave 2 Scene/Shot Creative-Output Validation

## Scope

`MovieWave2CreativeOutputValidator` is a provider-neutral validation boundary for structured Scene and Shot planning output. It is separate from the existing Movie Director Story validator so Story assistance rules cannot be mistaken for production planning rules.

The validator accepts a versioned `MovieWave2CreativeOutput` and a server-assembled `MovieWave2CreativeOutputValidationContext`. It does not call a provider, create a generation job, charge a customer, or manufacture replacement creative content.

## Deterministic checks

The validator rejects output for:

- unsupported schema version, malformed JSON, conflicting aliases, or null structured entries;
- missing project, language, scene, shot, identity, order, title/summary, description, or duration fields;
- empty output and over-bounded scene, shot, or text lengths;
- duplicate scene identity/order/content and duplicate shot identity/content;
- duplicate or non-positive shot order;
- non-positive, over-bounded, or shot-total-over-scene durations;
- unsupported scene/shot categories, Movie status values, production stages, quality levels, or cinematography intent values;
- source project/scene/shot mismatch and target project/scene/shot mismatch;
- stale base revision, base version, or context version;
- explicit locked-canon claims whose value differs from a matching locked constraint, plus obvious textual contradictions such as `instead of <locked value>`;
- obvious template/placeholder markers;
- unsupported output languages and deterministic English/Arabic script mismatches.

It does not score subjective artistic quality, narrative taste, originality, visual appeal, or cinematic quality.

## Repair and failure behavior

`MaxRepairAttempts` is clamped to `0..1` and defaults to `0`. If enabled, the only repair is removal of exact duplicate structural scene/shot entries. It preserves the first entry and keeps all creative text unchanged. It never fills fields, truncates text, changes identity/order/duration, resolves canon conflicts, translates content, or retries a provider.

Every non-structural failure returns `Rejected` with `Output == null`. This makes invalid creative output fail honestly and prevents downstream code from treating an unsafe proposal as usable.

## Integration

- `IMovieWave2CreativeOutputValidator` is registered in `Program.cs`.
- `MovieWave2CreativeOutputSchema.Json` is the compact provider-facing shape; semantic checks remain server-side.
- `MovieWave2CreativeOutputValidationContext` must be assembled from authorized current project state before validation. The context owns the expected source, target, current base, language, known identities, and locked canon.
- No database migration is required.
- Existing production configuration keeps customer charging disabled and movie providers disabled; this layer does not alter either setting.

## Focused test command

```text
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --filter FullyQualifiedName~MovieWave2CreativeOutputValidationTests
```

The focused suite covers valid output, empty/malformed/schema failures, required fields and bounds, duplicate scenes/shots/order, invalid durations and enums/categories, source/target/stale-base mismatches, locked-canon conflicts, generic/template output, language mismatch, and bounded structural repair.
