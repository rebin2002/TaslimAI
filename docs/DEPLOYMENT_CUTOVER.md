# Deployment cutover readiness

Scope: the zero-downtime cutover behaviour of the two production Railway services
(`Taslim API`, `Taslim Web`) around a `main` release. This page defines the
readiness gate, the configuration it depends on, and the rollout and recovery
path. It does not change any runtime behaviour and records no production mutation.

Companion tooling: [`scripts/deploy-readiness-gate.sh`](../scripts/deploy-readiness-gate.sh)
and its contract tests
[`scripts/deploy-readiness-gate.test.sh`](../scripts/deploy-readiness-gate.test.sh).

## 1. Observed, inferred, and unresolved

Keeping these three apart matters, because the remedy differs for each.

### 1.1 Observed

Recorded from the release record and from read-only `GET` requests made from this
lane on 24 Nov 2026. Nothing here required a write, a secret, or a provider call.

| Signal | Evidence |
| --- | --- |
| Merge that triggered the release | PR #1245 merged at 16:32:10Z, main `1e2cb80a015165564392cad87d8ab0a4b037a376`, delta 8 files `+83/-1`, **zero migrations** |
| API deployment | `cc964a58-4900-47b2-b31b-cb941d86bfc2` reached `SUCCESS`; `BUILDING -> DEPLOYING -> SUCCESS` |
| Web deployment | `68920115-7e2c-46b9-b483-459e80cb3b2b` reached `SUCCESS` on the same SHA |
| The transient | deployment-scoped HTTP logs show 54 API requests of which **3 returned HTTP 502 on `/health`**, confined to **16:33:26.723Z - 16:33:30.285Z** (a 3.562 second window); 80 Web requests with 0 5xx; every later sampled request clean |
| Post-cutover state, sampled by this lane at 17:52Z | `/health` 200 `alive`, `/health/live` 200 `alive`, `/health/ready` 200 `ready` with `database=available` and `storage=available`, `/readiness` 200 `ready`; Web `/`, `/login`, `/register` all 200 with no redirect |
| Gate run against production at 17:52Z | `deploy-readiness-gate.sh` returned 0: API green after 3s, Web green after 2s |
| Config-as-code inventory | `git ls-files` finds **no** `railway.json`, `railway.toml`, or `.railway/` on `main`. The only Railway contract in the repository is prose (README `## Railway deployment`, `docs/OPERATIONS_RUNBOOK.md`) plus the container smoke gates |

The transient is bounded and self-resolved. It is not an outage and does not
justify emergency change; it justifies a gate.

### 1.2 Inferred, and the measurements that support it

The 502 cannot have been produced by the application. The application has no code
path that returns 502 on `/health`: `/health` and `/health/live` return `Results.Ok`
unconditionally once routed, and `/health/ready` returns 200 or 503
(`apps/api/Program.cs:567-599`). A process that has not bound its socket cannot
emit any status line, so a 502 inside the cutover window is the platform proxy
answering **on behalf of** a container that was not yet listening.

Why there is such a window is visible in the startup order. `Program.cs` configures
middleware and maps every endpoint, then applies migrations, and only then calls
`app.Run()`:

```text
app.MapControllers()                 // routes configured, not served
if (ApplyMigrations)                 // true in Production: appsettings.Production.json
    DatabaseMigrator.ApplyAsync(...) // opens PG connection, takes pg_advisory_lock, MigrateAsync
    AdminRoleBootstrapper...
app.Run()                            // Kestrel binds the port here, last
```

With `Database:ApplyMigrations=true` in Production, the HTTP port exists only after
the advisory lock is acquired, the whole migration chain is walked, and the
administrator bootstrap finishes. Anything routed to the container before that has
no upstream.

Reproduced locally against PostgreSQL 16 on loopback, same image entrypoint, no
external endpoint reachable (`Files:S3Endpoint=https://object-storage.invalid`),
built from `1e2cb80a`:

| Case | Posture | Time to first HTTP 200 on `/health/live` | Refused probes in the window |
| --- | --- | --- | --- |
| C | `ApplyMigrations=true`, schema already current (the #1245 shape) | **8.1s** | 30 |
| B | `ApplyMigrations=false`, same database | 7.6s | ~30 |
| A | `ApplyMigrations=true`, fresh schema, 82 migrations, 148 tables created | **21.6s** | ~100 |

Two conclusions, both measured rather than assumed:

1. In case C the window is ~8s, and 3 refused probes spaced over 3.5s fits inside
   it. The observed transient is consistent with the pre-bind window.
2. A schema walk is **not required** to produce the window: case B still waits ~7.6s,
   because Kestrel binds last regardless. Migration time widens the window (case A,
   ~21.6s) but does not create it.

Therefore the inferred root cause is **not** "migrations are slow" and **not** a
defect introduced by #1245. It is that promotion to the new revision is not gated
on the revision actually serving, so in-flight requests can reach a container that
has no listener. The migration chain is an amplifier, not the cause.

### 1.3 Unresolved, and how to settle it

This lane cannot see the Railway service settings, so the following stays open
until someone reads the dashboard. Reading is not mutating.

1. **Is a healthcheck path configured on either service?** With `healthcheckPath`
   set, Railway polls the new container and switches only on a 2xx, so the previous
   deployment keeps serving and no proxy 502 is possible. The observed 502s are the
   expected signature of **no healthcheck path**, or of one pointed at the wrong
   port. Settle it on the dashboard: service `Settings` pane (`Deploy` section) and
   the deployment detail page, which shows a file icon for any value that came from
   config in code.
2. **Is the healthcheck port right?** Railway probes the injected `PORT`. The API
   image pins `ASPNETCORE_HTTP_PORTS=8080` (`apps/api/Dockerfile:19`) and does not
   read `PORT`. If the service port and the probe port disagree, Railway reports
   `service unavailable`. Confirm `PORT=8080` on the API service, or record the
   target-port setting that makes them agree.
3. **Who produced the 3 requests?** Only the request pattern distinguishes a platform
   probe, the release smoke, and browser traffic. Compare `requestId`/`X-Request-ID`
   in the deployment logs against the reviewer's smoke run.

## 2. The gate contract

`deploy-readiness-gate.sh` promotes a cutover only when both services are
demonstrably serving. Its verdict taxonomy is deliberate: an application 503 and a
proxy 502 mean opposite things and must not be merged into one failure.

| Verdict | Meaning | Promote? |
| --- | --- | --- |
| `ready` | 2xx and every `required: true` check is `available` | yes |
| `not-ready` | bound, but a required dependency check is red | no; investigate the dependency |
| `no-upstream` | refused / timeout / empty reply, or a 5xx carrying no Taslim readiness document | no; the revision never bound |
| `throttled` | 429 from the health limiter | no; back the probe rate off |
| `redirect`, `http-N`, `bad-body` | route or contract drift | no; inspect before retrying |

Endpoints used, and why they are the right ones:

- **API `/health/ready`.** Dependency-gated by construction: `database` via
  `CanConnectAsync` and `storage` via a read-only `ExistsAsync`, each bounded by
  `Health:ProbeTimeoutSeconds=5`, and it returns 503 during host shutdown so a
  draining instance cannot be promoted. It is anonymous, it is cache-free, and it
  makes **no paid provider call** and **no write**.
- **Web `/`.** Public, unauthenticated, non-paid: the home document is client-gated
  by `ProtectedPage`, so an anonymous `GET /` returns 200 HTML without contacting
  the API or any provider. This is the same terminal response that
  `scripts/web-container-smoke.sh` already certifies (200, `text/html`,
  `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`), so the platform gate
  and the CI gate judge one standard. `/login` is the fallback if `/` is ever
  server-gated, because a redirect is not a 2xx and would wedge the cutover.
- **Never `/api/*` on Web.** That is the same-origin proxy to the API; gating Web on
  it would couple Web's health to API dependencies and let an API storage blip mark
  a healthy Web container unhealthy.

Advisory checks never block promotion. `worker` is being added as
`required: false` in lane #1028, and the gate already honours that contract: it
fails only on checks whose `required` is literally `true`. A degraded worker is a
reason to investigate, not a reason to keep the old revision serving.

## 3. Configuration change

### 3.1 Do not add `railway.json`

Railway **Config as Code** (`railway.json` / `railway.toml`) is deprecated and its
files stop being read on **2026-12-01**, a week from this writing; new services
cannot opt into it. Adding one now would buy one deployment of durability and then
silently stop applying. The supported vehicle is **Infrastructure as Code**,
`.railway/railway.ts`, evaluated by the CLI as `plan` then `apply`.

The immediate remedy therefore needs **no repository change at all**: it is two
dashboard values.

### 3.2 Exact settings to apply, and their values

| Service | Setting | Value | Why |
| --- | --- | --- | --- |
| Taslim API | Healthcheck Path | `/health/ready` | gates promotion on dependencies, not on process liveness |
| Taslim API | Healthcheck Timeout | `300` | documented default; case A needed 21.6s locally, so 300s is a wide margin |
| Taslim API | Restart policy | `ON_FAILURE`, max retries `10` | unchanged from the platform default; a failed healthcheck marks the deploy failed, it does not restart a healthy process |
| Taslim Web | Healthcheck Path | `/` | public, non-paid, matches the existing smoke contract |
| Taslim Web | Healthcheck Timeout | `120` | `next start` binds without a migration chain |
| both | Overlap seconds | `30` | keeps the previous deployment answering while clients finish in-flight work |
| both | Draining seconds | `10` | graceful SIGTERM window; the readiness endpoint already returns 503 while stopping |

Equivalent IaC, for when the project is migrated deliberately (this file is **not**
added by this lane, because `railway config apply` would take ownership of the whole
project graph and must not be attempted blind):

```ts
import { defineRailway, project, service } from "railway/iac";

export default defineRailway(() => project("taslim", {
  resources: [
    service("Taslim API", { healthcheck: "/health/ready", healthcheckTimeout: 300 }),
    service("Taslim Web", { healthcheck: "/", healthcheckTimeout: 120 }),
  ],
}));
```

### 3.3 Verify in a staged environment first

No production setting is changed by anything in this repository. Verify before
promoting, on a **separate Railway environment or a throwaway project**, never on
the production environment:

1. `railway link` to the staged project, then `railway config pull --force` and
   inspect it, so the staged graph is known rather than assumed.
2. Set the API healthcheck path to `/health/ready` and the Web path to `/` on the
   staged services only.
3. Deploy the candidate SHA to staging and read the deployment detail page: the
   healthcheck value must show, with the file or settings icon that names its
   source.
4. Confirm the gate **holds** rather than passes trivially: with the staged API
   pointed at an unreachable database the deployment must not go `SUCCESS` before
   the timeout, and `deploy-readiness-gate.sh` against staging must exit 1 with
   `not-ready`. A gate that cannot fail is not a gate.
5. Confirm promotion timing on a clean staged deploy: the previous deployment must
   still answer throughout the startup window, i.e. zero 502 on `/health` in the
   deployment-scoped logs.
6. Only then apply the same two values to production through the dashboard.

Staged check with the gate, with `GATE_SKIP_WEB=1` for an API-only environment:

```bash
bash scripts/deploy-readiness-gate.sh "$STAGED_API_URL" "$STAGED_WEB_URL" 300
```

## 4. Rollout

Preconditions: the candidate SHA has hosted CI green on the exact head and on the
merge ref; `scripts/deploy-readiness-gate.test.sh` passes; the release evidence
manifest for the SHA is retained; a rollback target is named in advance.

1. Record the current coordinates before promoting: main SHA, and the **currently
   active** `SUCCESS` deployment id for each service. The active deployment is the
   rollback target, and it stops being obvious once a new one appears.
2. Confirm both healthcheck settings from section 3.2 are present on the production
   services. If the API path is unset, set it before the release rather than during it.
3. Merge through the normal reviewed path. Each service then deploys independently:
   Railway applies no startup ordering to GitHub push deploys, so Web may switch
   while the API is still migrating. The gate in step 4 is what closes that gap.
4. Immediately after the API deployment leaves `DEPLOYING`, run the gate against
   production with a 300s budget and read the verdicts rather than the deployment
   status:

   ```bash
   bash scripts/deploy-readiness-gate.sh \
     https://taslim-api-production.up.railway.app \
     https://taslim-web-production.up.railway.app 300
   ```

5. Green means: sample the deployment-scoped HTTP logs for the cutover window and
   record that 5xx on `/health` and `/health/ready` is zero. Record the deployment
   ids and this run next to the release evidence.
6. Red means: do not "wait and see", and do not restart anything. The previous
   deployment is still serving. Go to section 5.

## 5. Recovery

Choose by verdict; the three cases need opposite responses.

| Gate verdict | Meaning | Action |
| --- | --- | --- |
| `no-upstream` after the full budget | the revision never bound; startup is blocked on the advisory lock or a migration | **Abort/Redeploy is not the first move.** Read the API startup logs for `Taslim.DatabaseMigration`. If a migration failed, fix forward through the reviewed path. `pg_advisory_lock` is held per session and released when the connection closes, so a wedged predecessor is cleared by stopping the old deployment, not by killing the database |
| `not-ready`, `database=unavailable` | the schema target is unreachable | check Railway PostgreSQL status; do not roll code back for an infrastructure fault |
| `not-ready`, `storage=unavailable` | the R2 adapter is not answering | `Health:StorageRequired=true` makes this a promotion blocker on purpose; verify R2 settings and provider status before touching the deployment |
| `throttled` | probe budget exhausted | widen the gate interval; if PR #249 has landed, re-check its per-client-IP budget before promoting |
| gate green but the release is wrong | a functional regression, not a cutover failure | roll back |

Rollback, per service, from `Service -> Deployments`, the three-dot menu on the
previous deployment:

- **Rollback** reverts to the previously **successful** deployment and restores both
  the Docker image and that deployment's custom variables. Use this for a bad
  release. Deployments older than the plan retention policy cannot be rolled back to,
  which is why the rollback target is recorded in step 4.1 rather than looked for later.
- **Redeploy** re-creates the same code and build/deploy configuration as a new
  deployment. Use this for a cutover that never bound, or to retry a failed
  healthcheck after a configuration fix.
- **Abort deployment** cancels one still in progress.

For `1e2cb80a` (PR #1245) specifically: the delta carried **zero migrations** and its
evidence manifest reproduces `migrations.fingerprintSha256`
`13cdf6061119db16a44d902b6ea720e8031f6b6d4f2901557111ff07c2aef642` unchanged from the
pre-uplift manifest, so a code rollback of that API deployment is database-safe.
That is a property of that batch, not a general rule: `docs/RELEASE_EVIDENCE.md`
requires comparing `migrations.fingerprintSha256` before any rollback, and a
different fingerprint means the schema posture must be reviewed first. Never reset,
drop, or recreate a production database as part of this workflow.

## 6. Lane coordination

Nothing here duplicates or supersedes an existing owner. Deliberate non-goals:

| Lane | Ownership | Interaction with this gate |
| --- | --- | --- |
| **#1028** (open, `MERGEABLE`, base `0325c2d2`, 6 merges stale) | adds advisory `worker` (`required: false`) to the readiness document | this lane adds **no** check to `ReadinessAsync`, touches no `.cs`, and preserves the non-gating contract the PR depends on; the gate's `advisory-degraded` test pins it |
| **#249** (open, `MERGEABLE`, base `d3fffca5`) | health rate limit: 2 concurrent and 30/min per client IP on `/health`, `/health/live`, `/health/ready`, `/readiness` | **live interaction.** Platform probes originate from `healthcheck.railway.app`, not the public interface, so the cap does not bind them; this lane's default retry is 5s (12/min) to stay inside the budget, and the gate has a distinct `throttled` verdict |
| **#66** (open, **`CONFLICTING`**, base 2026-10-05) | bounds startup migration time via `Database:MigrationTimeoutSeconds` | **not duplicated.** Measurement B shows the window exists without migrations, so bounding migration time is necessary-but-not-sufficient; #66 also edits `Program.cs`, `README.md` and `docs/OPERATIONS_RUNBOOK.md`, so any ordering change here would collide with it |
| #726, #284 | staging rollback contract tests, release-governance evidence | rollback steps in section 5 are the operational counterpart; no test-lane files touched |
| #258, #461 | readiness redaction, provider failure status | untouched |

File footprint of this lane: `scripts/deploy-readiness-gate.sh`,
`scripts/deploy-readiness-gate.test.sh`, one added step in the `smoke-contracts` job
of `.github/workflows/ci.yml`, and this document. No `.cs`, no `appsettings*`, no
Dockerfile, no `Program.cs`, no `README.md`, no `docs/OPERATIONS_RUNBOOK.md`, so the
diff is disjoint from #1028, #249 and #66.

## 7. Verification performed

Local, offline, from `1e2cb80a`; no production resource and no paid provider call
was touched.

- `bash -n scripts/deploy-readiness-gate.sh` - clean.
- `bash scripts/deploy-readiness-gate.test.sh` - **18 passed, 0 failed**, covering
  green promotion, advisory degradation staying green, a red required check
  blocking, proxy 502 versus application 503 disambiguation, 429 handling, Web
  failing independently of a green API, a closed port reporting `no-upstream`,
  API-only operation, and the usage guards.
- `bash scripts/api-health-smoke.test.sh`, `scripts/container-health-smoke.test.sh`,
  `scripts/web-container-smoke.test.sh`, `scripts/readme-health-contract.test.sh` -
  all still pass, so the existing health contracts are unbroken.
- Startup window measured three ways against PostgreSQL 16 on loopback (section 1.2).
- Read-only `GET` probes of the four API health routes and three Web routes;
  `deploy-readiness-gate.sh` against production exit 0 (section 1.1).

Not done, and why: the container smoke scripts require the Docker CLI, absent from
this sandbox, and Railway project access would be a production mutation. Both are
recorded as preconditions in section 3.3 rather than assumed green.
