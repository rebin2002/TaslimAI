# Password-recovery release checklist — PR #686

**Disposition: HOLD.** This is an operator checklist, not authorization to configure SMTP, mutate any live database, merge, or deploy. Keep recovery disabled and unadvertised until every required gate below has a named owner, dated evidence, and formal independent review. Never put mailbox passwords, Railway secrets, reset tokens, recipient addresses, or raw SMTP diagnostics in chat, source control, shell history, screenshots, or logs.

Baseline before this change: PR #686 was open at `f8c8795ddfb13a97e19361aa4e8739225e8c0784` with 7/7 hosted checks green and zero formal approving reviews. Any new commit requires the full required hosted checks on that exact new head.

## 1. Assign accountable owners before execution

| Workstream | Accountable role | Evidence required to close |
|---|---|---|
| PR code, provider-free tests, exact-head CI | Auth-recovery PR owner | Reviewed diff; focused and full CI results on one immutable SHA |
| Staging origin, hardening, and real-ingress rate limits | Staging/release operator | Isolated staging inventory; origin-policy tests; two-client and spoofed-header evidence |
| InterServer relay and controlled mailbox receipt | Railway API-service operator plus mailbox administrator | Redacted API-side STARTTLS/auth result and one controlled test-mailbox receipt; no credential disclosure |
| Database backup, restore, migration, and rollback | Authorized PostgreSQL operator | Backup/PITR metadata plus restore-to-separate-database comparison and rollback evidence |
| Independent security review | Reviewer other than the PR author | Formal GitHub **APPROVED** review of the final exact commit; a comment is not approval |
| Release decision and evidence ledger | Release Coordinator | Named owners, linked evidence, explicit GO/HOLD and separate release authorization |

Current access limitation: the repository collaborator list contains only `rebin2002`; #686 has zero formal approvals, and `main` is not branch-protected. The Release Coordinator must obtain an authorized independent reviewer; do not substitute an author review or weaken the review gate.

## 2. Gate A — local/provider-free code checks

Run these from the repository root on the candidate commit. These commands use the API test fakes and do not contact SMTP or paid generation providers:

```bash
dotnet test apps/api.Tests/Taslim.Api.Tests.csproj \
  --configuration Release \
  --filter 'FullyQualifiedName~PasswordRecoveryTests|FullyQualifiedName~PasswordResetEmailWorkerConcurrencyTests|FullyQualifiedName~PasswordRecoveryLeasePolicyTests|FullyQualifiedName~PasswordResetEmailWorkerLeaseExpiryTests'

dotnet test apps/api.Tests/Taslim.Api.Tests.csproj --configuration Release
npm test --workspace @taslim/web
npm run lint --workspace @taslim/web
npx tsc --noEmit -p apps/web/tsconfig.json
(cd apps/web && npx playwright test e2e/auth.spec.ts --project=chromium)
```

Pass criteria: no failed tests; the focused recovery/worker cases cover generic unknown/known-account responses, reset expiry/replay rejection, disabled readiness, sanitized fake-sender failure/retry, active-lease behavior, lease-policy boundaries, and stale-writer fencing after a controlled lease expiry. The configured lease floor is 60 seconds (15-second sender timeout plus a 45-second processing margin); readiness fails closed below it. This is a mitigation for the short-lease configuration, **not** a hard end-to-end execution bound: arbitrary process/scheduler pauses, database delays, or an SMTP operation that does not return promptly can still outlive a finite lease. The fake-sender test deliberately demonstrates the resulting at-least-once duplicate-acceptance window while checking that stale completion does not overwrite the newer `Delivered` state. These local tests do not prove real SMTP delivery, actual timeout behavior under live runtime conditions, or exactly-once email.

Then require all current hosted PR checks on the same SHA: API integration, frontend, Browser E2E, production-image/API smoke, deployment-helper contracts, release-evidence contract, and Autopilot gate. Capture run URLs and API test count. Do not rerun against a moving or superseded head and call it final.

## 3. Gate B — isolated staging security and origin

Before sending any message:

1. Create/identify a staging API service with a separate database, storage namespace, OAuth/application credentials, and SMTP secret scope. Confirm no staging route or reset link can reach production data or `https://taslim.ai`.
2. Preserve production-equivalent controls. Prefer `ASPNETCORE_ENVIRONMENT=Production` on the isolated stage plus a separately reviewed, finite recovery-origin context. The current code pins Production reset links to `https://taslim.ai` and rejects `https://staging.taslim.ai`; nonproduction currently accepts only `taslim.ai`, `localhost`, and `127.0.0.1`. Do **not** bypass this by putting the production hostname into stage. If ASP.NET `Staging` is used instead, first prove proxy trust, HSTS, secure/SameSite cookies, antiforgery, and explicit migration control are equivalent; the environment name alone is not proof.
3. Keep `PasswordRecovery__Enabled=false`. Verify `GET /api/auth/password-recovery/availability` returns `{"available":false}` and the login link and request/reset forms remain hidden. Verify a direct API request is rejected while disabled.
4. Through the real staging ingress, use two independent controlled clients. Confirm the app-visible client partition does not collapse both clients into one quota, one client cannot exhaust the other's limit, edge abuse controls use a trusted signal, spoofed arbitrary `X-Forwarded-For` cannot bypass those controls, and no alternate ingress bypasses them. Retain only minimized evidence; do not retain raw client IPs unnecessarily.

**Stop gate:** the current PR does not allow the staging hostname as a Production recovery origin, and the actual ingress behavior has not been verified. A host setting alone cannot fix this; the origin needs a separately reviewed code/policy change with production-host pinning regression tests and exact-head CI. Do not set a staging base URL that the current code rejects, route stage reset links to production, or continue to a real mailbox test until both origin and ingress gates are resolved and reviewed.

## 4. Gate C — migration, backup, restore, and rollback

1. On the exact candidate source, enumerate registered migrations:

   ```bash
   dotnet ef migrations list \
     --project apps/api/Taslim.Api.csproj \
     --startup-project apps/api/Taslim.Api.csproj \
     --context TaslimDbContext
   ```

2. Resolve the known registry discrepancy before staging deployment: source contains `20261005112000_AddImageActiveJobGuard.cs`, but EF did not register that migration and rejected it by ID. The local synthetic rehearsal therefore used registered predecessor `20261005092138_EnforceOneActiveSubscriptionPerWorkspace`. The migration owner must state whether the file is intentionally orphaned or supply reviewed discovery metadata, then rerun migration-list/apply/rollback checks. Do not silently treat the local 82-entry history as proof that the canonical production sequence is resolved.
3. An authorized database operator verifies the staging target identity, takes the platform-supported backup/PITR point, and restores it to a **separate** isolated database. Compare migration history, expected outbox table/index/FK, and a synthetic marker set before/after. Never restore over production or run `Down` against a live database.
4. Apply `20261029070409_AddPasswordResetEmailOutbox` to isolated staging only through the approved migration process. Verify the exact history ID and schema. In a disposable restored copy only, test app-image rollback plus schema rollback/reapply. The migration `Down` drops the outbox table; before any rollback, an authorized operator must establish whether it contains data that must be preserved.
5. Record backup timestamp/retention, restore start/end, schema/history comparison, app/schema rollback result, operator, and evidence link. The already-passed local PostgreSQL rehearsal is useful synthetic evidence only; it does not prove Railway production backup/PITR or object/R2 recovery.

Production startup currently applies pending EF migrations when `Database:ApplyMigrations` is true, independently of the recovery feature flag. Therefore `Enabled=false` is not a migration safety switch. Do not deploy until the migration registry issue and backup/restore/rollback gates are closed.

## 5. Gate D — InterServer STARTTLS and one controlled reset

The repository sender supports authenticated STARTTLS on port 587; port 465 implicit TLS is explicitly unsupported. InterServer's reply to date was “Yes, please use the settings and let us know,” which is tentative support confirmation, **not** a connectivity/authentication or delivery result.

After Gates A–C pass, a designated operator—not this agent—may, with separate staging authorization, configure secrets only on the **staging API service** through its private secret manager:

| Variable | Staging value/policy |
|---|---|
| `PasswordRecovery__Enabled` | `false` until all preflight gates are signed off; enable only for the controlled staging test |
| `PasswordRecovery__PublicBaseUrl` | Exact reviewed staging HTTPS origin, only after a reviewed code change permits it; never `taslim.ai` for stage |
| `Email__Provider` | `Smtp` |
| `Email__Host` | `webhosting1900.is.cc` |
| `Email__Port` | `587` |
| `Email__EnableSsl` | `true` (STARTTLS) |
| `Email__Username` | Staging-scoped SMTP credential username; not a production mailbox credential |
| `Email__FromAddress` | Provider-authorized staging-only sender identity |
| `Email__FromName` | `Taslim.ai` |
| `Email__Password` | Operator enters a staging-scoped credential privately in the secret manager; never place a production credential or any value in chat, command line, PR, or logs |

Do not copy the production `no-reply@taslim.ai` password into staging. The current known InterServer account/credential is not documented as staging-scoped. If no distinct staging credential and sender identity can be obtained, mark controlled-mailbox staging proof **BLOCKED** until the account/security owner approves a narrowly scoped exception; do not silently reuse production credentials.

After Gates A–C pass **and** the reviewed staging origin and staging-scoped mail identity exist, a designated operator may make one separately authorized API-side authenticated send to a controlled mailbox that is not a customer account. Verify relay TLS/authentication, staging sender identity, exactly one expected receipt for the one request, timestamp, stage-only reset-link host, and no credential/token/recipient/SMTP exception leakage in application or proxy logs. From the test account, verify valid reset changes the password, invalidates replay and expired tokens, does not auto-sign-in, and invalidates the prior session as designed. Repeat a known/unknown-address request comparison; externally visible response must remain indistinguishable. If the send fails, keep/return the staging flag to `false`, retain only sanitized failure code and run metadata, and follow the existing bounded retry evidence—do not paste SMTP diagnostics into chat.

After the controlled test, set staging `PasswordRecovery__Enabled=false` unless the separate release decision explicitly authorizes a staged demo. No production variables or secrets are part of this checklist.

## 6. Gate E — final approval, rollout, and stop/rollback criteria

Before any release consideration, the Release Coordinator assembles one evidence bundle containing: immutable PR SHA; all seven exact-head checks; focused/full tests; staging-origin and ingress results; migration registry disposition; staging database backup/restore and rollback evidence; redacted STARTTLS plus controlled-mailbox proof; and formal independent security approval. The independent reviewer must inspect the **final SHA** and submit GitHub **APPROVED**; comments, green CI, or local harnesses do not substitute.

**GO only after** all evidence is attached, the authorized reviewer has approved, the owner signs the release decision, and the user separately approves any merge/deployment action. At deployment, keep recovery disabled initially, verify health/migration state, then enable only under an explicitly approved controlled rollout. **HOLD/rollback immediately** on wrong reset-link host, shared-ingress quota leakage, failed/ambiguous relay delivery, token/log leakage, migration mismatch, failed backup/restore, or non-green exact-head checks. Disable the feature flag first; image rollback alone is not schema rollback. Do not run destructive schema rollback without a verified backup and an operator-confirmed outbox data disposition.

## Current ownership handoff

- **Auth recovery/code-test owner:** maintain the existing #686 branch, keep the lease floor and provider-free tests green, and publish the final exact-head CI receipt. The previously denied synchronized-`Pending` test file remains untouched.
- **Staging design owner:** close the exact staging-origin/production-equivalent-hardening and real-ingress evidence; coordinate the unregistered migration file with its original migration owner. Their separate local staging guide is not included or published by this checklist.
- **Release Coordinator:** assign a named Railway API/mailbox operator for the controlled 587 test; assign an authorized PostgreSQL operator for real backup/PITR restore evidence; obtain a distinct independent security reviewer with GitHub approval rights; track each evidence item and keep HOLD until complete.
- **Repository owner:** provide separate explicit approval for any eventual merge/deploy. No production configuration, database, email, or deployment action is authorized here.
