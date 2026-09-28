# Movie Director Creative Output Validation

## Scope

Task 16 adds a deterministic quality boundary around Movie Director Story assistance output. It validates the typed `DirectorStoryActionPayload` before a proposal is persisted and validates the payload again immediately before an approved action creates a new Story revision.

The implementation is provider-neutral. It does not add a provider, a second retry system, billing behavior, or autonomous execution.

## Validation coverage

`MovieDirectorCreativeOutputValidator` checks:

- structured JSON round-trip/schema shape;
- action-specific required fields and non-empty output;
- existing Story limits for premise, logline, synopsis, treatment, replacement text, screenplay fields, and scene elements;
- placeholder and template-like output markers;
- overlap with the bounded Movie Director context;
- dialogue character names against bounded Cast and screenplay names;
- target scene/element identity for passage edits;
- repeated sentences, duplicate fields, and duplicate scene elements;
- unsupported claims that locked canon confirms content not present in locked context;
- output script against the Movie Project language (`en`, `ar`, or `ku`);
- base revision identity so a stale proposal cannot be treated as current canon.

## Repair and failure behavior

Only one local structured repair is allowed, controlled by `MovieDirectorCreativeValidation:MaxRepairAttempts` and clamped to `0..1`. The repair removes exact duplicate screenplay elements when that is the only deterministic repair. It does not invent missing creative text, truncate content, reinterpret canon, or retry a provider.

Unrepairable output is rejected with a safe user message:

> The Director could not produce a usable Story proposal. Try a clearer goal or add the missing locked context.

The internal reason codes are logged using action, code, and count only. When an approved action fails validation, up to 16 reason codes are stored in the existing bounded `DirectorHistoryEvent.SafeDetailsJson`; raw output, prompts, provider payloads, credentials, and exception details are not returned to the user.

Wave 5 remains the owner of provider retry/fallback semantics. If a future provider-backed Director adapter is added, it should map an output rejection to the existing `ProviderAttemptResultCategory.ValidationRejected` and let the existing bounded orchestrator decide whether another provider attempt is allowed. This task does not create a new retry architecture or charge for local repair.

## Integration points

- `MovieDirectorStory.cs`: planner validates generated Story proposals before persistence.
- `MovieDirectorStoryExecutor.cs`: executor revalidates against the current bounded context before applying an approved proposal.
- `MovieDirectorServices.cs`: safe internal reason codes are recorded in existing Director history.
- `MovieDirectorController.cs`: proposal validation returns HTTP 422 with an actionable provider-neutral message.
- `Program.cs` and `appsettings.json`: validator and bounded repair options are registered.
- Existing `DirectorStoryBoundedContextDto` now carries the Movie Project language.

## Persistence and migrations

No migration is required. Validation findings use the existing bounded `DirectorHistoryEvent.SafeDetailsJson` field and existing action failure fields. No billing, usage ledger, provider-attempt, or customer-facing schema is changed.

## Focused test command

The intended focused command is:

```text
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --filter FullyQualifiedName~MovieDirectorCreativeOutputValidationTests
```

The local .NET 8 SDK was installed in the sandbox for verification. The focused validator suite and existing Movie Director Story integration suite both pass.
