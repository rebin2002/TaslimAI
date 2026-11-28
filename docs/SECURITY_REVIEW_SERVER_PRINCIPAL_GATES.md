# PR #850 — bounded preparation record for zero-GUID principal guards

**Status:** implementation and regression-test preparation only. This record is not an independent review, a formal Security clearance, a GitHub approval, a merge authorization, or evidence of production deployment.

## Exact baseline and ownership boundary

- Live `main` at preparation start: `14848b17c70397aacb29a65145db21d5e864b9af`.
- Existing PR branch: `review/pr825-zero-guid-contract-20261116`, head before preparation: `98699f8a182b924c6ade7ce3adb280f6f9100c9c`.
- The branch was 16 commits ahead and 1,163 behind live `main`; GitHub reported a merge conflict.
- The existing PR #850 is the only target. No new PR is created, and the dormant umbrella #1232 is not edited or retargeted.
- The live ownership note identifies #1232 as overlapping the Activity and Notifications test surface, with no other inspected integration carrier sharing the three residual controller/test paths. The dedicated contract test uses a separate file and does not change #1232's files or scope.

## Live-main guard inventory

Direct inspection of live `main` found that the `TryGetUserId` helpers in these three controllers accepted a parseable `Guid.Empty`:

- `apps/api/Controllers/ActivityController.cs`
- `apps/api/Controllers/MemoriesController.cs`
- `apps/api/Controllers/NotificationsController.cs`

The existing guards in `FilesController`, `MovieDialogueController`, `MovieFinalAssemblyController`, `MovieSoundController`, and `UpscalingController` already rejected `Guid.Empty`; those live-main implementations were preserved unchanged.

## Forward-merge and final path inventory

Live `main` was forward-merged into the existing PR branch. All six textual/add-add conflicts were resolved by retaining the live-main versions first; only the three missing `Guid.Empty` conditions were then added. No existing main guard was weakened or replaced.

The intended diff against live `main` is limited to these five paths:

1. **Modified** `apps/api/Controllers/ActivityController.cs` — require both successful parsing and `userId != Guid.Empty`.
2. **Modified** `apps/api/Controllers/MemoriesController.cs` — same fail-closed condition.
3. **Modified** `apps/api/Controllers/NotificationsController.cs` — same fail-closed condition.
4. **Added** `apps/api.Tests/MalformedPrincipalEmptyIdentifierContractTests.cs` — one dedicated cross-controller regression contract; no existing umbrella test files are changed.
5. **Updated** `docs/SECURITY_REVIEW_SERVER_PRINCIPAL_GATES.md` — this preparation record, with no independent-review or clearance claim.

## Regression test and negative control

The dedicated test file exercises eight identifier forms: absent claim, empty string, whitespace, unparseable text, and `Guid.Empty` in dashed, compact, braced, and hexadecimal forms. It covers the 38 public actions in the eight controller areas, with a valid-identifier control that verifies a well-formed principal reaches the expected workspace-membership refusal rather than being rejected as unauthorized.

Using .NET SDK `8.0.428` (the repository CI target is .NET 8), the focused suite passed on the prepared tree: **65 passed, 0 failed, 0 skipped**. Command:

```bash
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj \
  --filter FullyQualifiedName~MalformedPrincipalEmptyIdentifierContractTests \
  --verbosity minimal
```

The synthetic negative control used a disposable worktree at the untouched live-main SHA above and only copied in the dedicated test file. It reproduced **12 failures, 53 passes, 0 skipped**: exactly the four zero-GUID spellings for Activity, Memories, and Notifications. The other malformed-principal cases and the valid-identifier control passed. This confirms the regression suite distinguishes the three missing guards from the five guards already present on main.

## Boundaries

This preparation does not claim that all malformed-principal paths in the repository are covered. It does not alter `main`, merge or close any PR, change approval state, modify production, change #1232, or provide independent Security review or formal clearance. Exact-head hosted workflow results and the updated commit SHA are reported separately on PR #850 after those checks complete.
