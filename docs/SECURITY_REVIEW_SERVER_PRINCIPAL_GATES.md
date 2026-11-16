# Security Review — Server-Side Malformed-Principal Gates (PR #825)

- **Reviewed artifact:** PR #825 `integration(security): consolidate malformed-principal fail-closed server gates (#683 #660 #659 #654 #634 #650 #665 #674)`, branch `integration/server-principal-security-20261115`, head `28b757ea`, 11 commits, 16 files.
- **Baseline reviewed against:** `main` at `7745c8ce`.
- **Review branch carrying this evidence:** `review/pr825-zero-guid-contract-20261116`.
- **Reviewer role:** independent inspection only. Nothing in this review was merged, approved, or force-pushed, and no approval was recorded on behalf of a human.

## 1. Scope and method

Eight API areas were named for independent inspection: Files, Memory, Movie assembly, Upscaling, Activity, Notifications, Sound and Dialogue. For each area the review answers four questions:

1. Is every public action of the controller behind a principal gate, or can any action still resolve an identity through an unchecked parse?
2. What exactly does the gate treat as malformed?
3. If the gate lets an identity through, is there a second check that catches it, and is that check authorization rather than accident?
4. Does the accompanying test evidence pin the behaviour that the gate actually claims?

Method: a static audit of all 38 public actions across the eight controllers, a read of the cookie-validation pipeline in `Program.cs`, a read of `WorkspaceAccessService`, and executable negative cases over an eight-value identifier matrix.

## 2. What PR #825 gets right

- **No action is left ungated.** All 38 public actions across the eight controllers resolve their identity through `TryGetUserId` and return `Unauthorized()` before touching a service, a database set, or an idempotency key. The previous shape — `Guid.Parse(claim ?? throw new InvalidOperationException(...))` — surfaced a malformed claim as an unhandled 500 and is gone from all eight files.
- **The gate is first.** In every action the principal check precedes scope lookup, membership evaluation, and any expensive or state-changing work. In `UpscalingController.Create` it also precedes job creation, so a malformed claim cannot enqueue billable provider work. `FilesController.Upload` moved the check ahead of the `file is null` validation, so a malformed claim no longer reveals which rejection path a request took.
- **Identity is resolved once.** Per-action double reads of the claim (`MemoriesController.Update` and `Delete` previously parsed the claim twice, once inside the LINQ predicate and once in the membership check) now use one local value, which removes the possibility of the two reads disagreeing.
- **`CanAccessAsync` takes the identity as a parameter** in `FilesController`, so file access decisions and the request's authenticated identity can no longer diverge.
- **The direction of the change is correct:** fail closed at the server boundary rather than relying on the web tier, which matches the route-gating pattern already merged in #824.

## 3. Finding SR-1 — the all-zeros identifier is treated as a real user in six of eight areas

**Severity:** low today, by construction; high as a latent class. **Status:** confirmed by execution.

Two of the eight areas define the gate as

```csharp
Guid.TryParse(claim, out userId) && userId != Guid.Empty;
```

namely `FilesController` and `MovieDialogueController` (and `MovieDirectorController`, already on `main`). The other six define it as a bare

```csharp
Guid.TryParse(claim, out userId);
```

so `NameIdentifier = 00000000-0000-0000-0000-000000000000` satisfies the gate and is handed to the service layer as an authenticated user identity. `Guid.TryParse` is format-tolerant, so the same value also passes in its compact, braced and hexadecimal spellings; all four spellings were tested.

This is not a hypothetical standard the reviewer imposed on the PR. The PR itself decides that the empty identifier is malformed: it carries the commits `fix(files): fail closed on empty authenticated user identifier` and `test(files): reject missing and empty authenticated user identifiers`, and pins `"00000000-0000-0000-0000-000000000000"` as an explicit `[InlineData]` case in `FilesControllerPrincipalTests` and `MovieDialogueControllerPrincipalTests`. Consolidating eight routes onto seven of eight identical gates, while the eighth and second disagree with the other six, is the inconsistency this finding records.

`Guid.Empty` is the value an unset user identifier defaults to. A gate that admits it is therefore only as closed as whatever check happens to sit behind it — which is precisely the property the rest of the PR is designed to remove.

## 4. Why SR-1 is not an open bypass today

Two independent layers currently absorb it, and both were read rather than assumed:

1. **Cookie validation.** `Program.cs` `OnValidatePrincipal` rejects an unparseable `NameIdentifier` and then calls `UserManager.FindByIdAsync`. There is no identity row with the zero key, so a real session carrying it is rejected and signed out before the action runs.
2. **Membership evaluation.** `WorkspaceAccessService.IsMemberAsync` is a strict two-column lookup on `WorkspaceMembers` with no owner fallback, no administrator short-circuit and no zero-value branch. Every service behind the six loose gates calls it (directly, or through an equivalent ownership query) before any read or write, so the zero identity resolves to a refusal.

The consequence is that the observable difference between the strict and loose gates is a status code, `401` versus `403`, and no route returns data or mutates state for a zero principal. The exposure is therefore the *next* endpoint: any action added to these six controllers that trusts its `userId` argument, which is the exact defect class #825 exists to eliminate, inherits an open gate on the day it is written. Closing SR-1 removes a defence that currently depends on the discipline of every future service.

## 5. Finding SR-2 — negative-case coverage is uneven across the eight areas

The claim-malformed matrix is applied to two areas and reduced to a single unparseable string in the other six:

| Area | Malformed cases pinned by #825 | Claim absent | Claim empty | All-zeros identifier |
| --- | --- | --- | --- | --- |
| Files | 4 | yes | yes | yes |
| Dialogue | 4 | yes | yes | yes |
| Activity | 1 | no | no | no |
| Memory | 1 | no | no | no |
| Movie assembly | 1 | no | no | no |
| Sound | 1 | no | no | no |
| Notifications | 1 | no | no | no |
| Upscaling | 1 | no | no | no |

The six reduced suites also use `null!` service dependencies, which proves the gate short-circuits but does not distinguish "refused" from "would have succeeded had the service been reachable".

## 6. Added evidence on this review branch

`apps/api.Tests/MalformedPrincipalEmptyIdentifierContractTests.cs` applies the full eight-value identifier matrix — absent claim, empty string, whitespace, unparseable text, and the zero identifier in dash, compact, braced and hexadecimal spellings — to all 38 actions across the eight areas: 64 negative cases plus one positive control.

The positive control is deliberate. It signs in a well-formed identifier and asserts `ForbidResult`, not `UnauthorizedResult`, so the hardened gate cannot be quietly over-tightened into locking legitimate sessions out of their own requests.

Measured behaviour:

- Against the review branch, which carries both the six gate completions and the new suite: **65 of 65 new cases pass**, and the eight suites from #825 pass alongside them (81 combined).
- Against the same branch with only the six one-line gate completions reverted: **exactly 24 cases fail**, being the six loose areas multiplied by the four zero-identifier spellings. Files and Dialogue are absent from the failure list, confirming they already refused the value. The remaining failures surface as `Assert.IsType() Failure: Value is not the exact type`, the six controllers returning a downstream refusal instead of `UnauthorizedResult`.

The suite therefore discriminates the defect rather than merely asserting alongside it.

## 7. Change carried on this review branch

Six one-line completions, each matching the form already used by `FilesController`, `MovieDialogueController` and `MovieDirectorController`:

- `apps/api/Controllers/ActivityController.cs`
- `apps/api/Controllers/MemoriesController.cs`
- `apps/api/Controllers/MovieFinalAssemblyController.cs`
- `apps/api/Controllers/MovieSoundController.cs`
- `apps/api/Controllers/NotificationsController.cs`
- `apps/api/Controllers/UpscalingController.cs`

plus the new test file and this document. No route, contract, policy, migration, provider setting or storage path was modified.

## 8. Blocker report — why this is not a `main`-based PR

The instruction was to land a narrowly scoped PR on current `main`, or report the blocker. The blocker is file ownership, and it is binding:

- The six files needing completion are precisely the files exclusively owned by the open PR #825. Re-implementing the gate on a `main`-based branch would recreate #825's hunks on the same lines and produce a guaranteed collision, or silently absorb a security PR that is still awaiting independent approval.
- The alternative single-point fix — rejecting the zero identifier once, in `Program.cs` `OnValidatePrincipal`, which would cover all 33 controllers that read `NameIdentifier` rather than these eight — is also blocked: `apps/api/Program.cs` is currently owned by open PRs #815, #720 and #686.

Both routes were checked against the full set of 200 open PRs before any edit was made. This branch therefore bases on `integration/server-principal-security-20261115` so that its diff is purely additive on top of #825's head, with no rewrite of another task's in-flight work and no fast-forward hazard.

## 9. Security approval gate — exact state

The requested gate does not currently exist in a form the platform can enforce, and this is the principal risk to a batch deployment:

- `main` has **no branch protection** (`/branches/main/protection` returns `404 Branch not protected`).
- The repository has **no rulesets** (`/rulesets` returns `[]`).
- There is **no `CODEOWNERS` file**, so no required owner review exists for `apps/api/Controllers` or any other path.
- PR #825 has **zero reviews and an empty `reviewDecision`**, while reporting `MERGEABLE`.

Any contributor with write access can therefore merge a security PR today with no recorded approval, and no GitHub check will refuse it. Enabling this gate is deliberately **not** performed here: it applies repository-wide and would immediately start refusing the in-flight merges of the other parallel tasks, which is an operator decision, not an agent's. The exact action for a human maintainer is:

```bash
gh api -X PUT repos/rebin2002/TaslimAI/branches/main/protection \
  -H "Accept: application/vnd.github+json" \
  -f 'required_status_checks[strict]=true' \
  -f 'required_status_checks[contexts][]=Deployment smoke helper contracts' \
  -f 'required_status_checks[contexts][]=Frontend checks' \
  -f 'required_status_checks[contexts][]=API integration tests' \
  -f 'required_status_checks[contexts][]=Browser E2E' \
  -f 'required_status_checks[contexts][]=Autopilot next-wave gate' \
  -f 'required_status_checks[contexts][]=Build production images and smoke-test API' \
  -f 'required_status_checks[contexts][]=Release evidence and safety contract' \
  -f 'required_pull_request_reviews[required_approving_review_count]=1' \
  -f 'required_pull_request_reviews[dismiss_stale_reviews]=true' \
  -f 'enforce_admins=false' \
  -F 'required_linear_history=true' -F 'restrictions=null'
```

A `CODEOWNERS` entry naming a security owner for `apps/api/Controllers/` and `apps/api/Program.cs` should accompany it, so that a malformed-principal change cannot be approved by a frontend-only owner.

## 10. Verdict on PR #825

**Sound to merge once SR-1 is closed, and it should not be merged as the final word on the zero identifier.** The consolidation is correct and complete in the sense that matters most: no malformed claim can any longer surface as a 500, and no reviewed action can reach its service layer without a resolved identity. SR-1 is a seven-character omission repeated six times, and SR-2 is the reason CI did not catch it — six of the eight suites never sent the value that the other two deliberately reject.

## 11. Boundaries respected

Nothing was merged, closed, approved, labelled or rebased. No force push. No branch protection or repository setting was changed. No customer charging, provider enablement, production secret, production data or deployment target was touched; `Ai__OpenAI__Enabled` and every provider flag remain as configured in the repository, and no paid provider was contacted. No security route other than the eight under review was opened, and the remaining 23 controllers that still parse `NameIdentifier` directly are recorded as out of scope for this branch rather than edited here.
