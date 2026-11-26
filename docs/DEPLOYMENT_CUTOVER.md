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
(`apps/api/Program.cs:586-608` on `bea15310`; `567-599` on the `1e2cb80a` tree these observations were actually taken from). A process that has not bound its socket cannot
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

**Chain length has since moved, so 21.6 s is a floor, not a current measurement.**
The three rows above were measured by building `1e2cb80a`. Current `main`
(`bea15310`, merged #1268) carries **83** migrations, the chain head being
`20261124065935_AddAcademicResearchIntegration` rather than
`20261005112000_AddImageActiveJobGuard`, so case A understates today's cold-start worst
case by one migration. This lane has no .NET SDK, no PostgreSQL and no Docker, so the
window was **not re-measured** on `bea15310` and no re-measured figure is claimed here.
Cases B and C are unaffected in kind: case B shows the window exists with no schema walk
at all, which is chain-length independent, and case C depends only on the schema already
being current.

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

**Independent corroboration from hosted CI.** The window is not an artefact of this
sandbox. The pre-existing `Production container smoke` workflow
(`.github/workflows/container-health-smoke.yml`) builds the real production image and
polls `/health/live` every 2 s via `probe_production_liveness`
(`scripts/container-health-smoke.sh:40-65`). For head `6b6897b9` of PR #1277
(run 45454549235, job 138660373543) that step logged:

```text
18:03:22.504Z  ##[group]Run bash scripts/container-health-smoke.sh taslim-api:release-smoke 8080 120
18:03:42.041Z  Production startup smoke passed on attempt 7
18:03:46.536Z  [api-health-smoke] /health/live passed on attempt 3
```

i.e. **~19.5 s between the smoke container starting and the first 200**, on GitHub's
runner against a real `postgres:15` service, plus a further attempt-3 wait in the same
job. This matters for three reasons: it confirms the pre-bind window on infrastructure
this lane does not control; it shows CI already *expects* that window and absorbs it
with retries, which is precisely the tolerance production traffic does not get; and it
places the measured worst case (~20-24 s) inside the 60 s API healthcheck floor
proposed in §3.2 with roughly 2.5x margin on the 82-migration figure. §1.2 of the first
draft called that 60 s value "300 s": 300 s is the gate script's own total-probe budget
argument used in §3.3, not the Railway `healthcheckTimeout`, and the two must not be
conflated — a platform timeout of 300 s would mask a genuinely wedged startup for five
minutes before failing the deploy. The reconciled value is the §3.2 floor of 60 s, and
it should be re-measured against the 83-migration chain before it is relied on.

### 1.3 Unresolved, and how to settle it

Provenance matters here, so it is stated per item: the first two were **resolved
after this document was first written**, by evidence this lane did not generate
itself. Comment #7141321366 section 4 relays `describe_service` output showing
that **both deploy configs carry no `healthcheckPath` and no `healthcheckTimeout`
and there are no staged changes**, with both public target ports at **8080**. That
is a second-hand read of the live platform, consistent with the 502 signature, but
this lane has not opened the dashboard and does not present it as its own
observation. Confirm independently before acting on it.

1. **Is a healthcheck path configured on either service?** *Resolved as no by the
   evidence above.* With `healthcheckPath` set, Railway polls the new container and
   switches only on a 2xx, so the previous deployment keeps serving and no proxy 502
   is possible. The observed 502s are the expected signature of **no healthcheck
   path**, or of one pointed at the wrong port. Re-confirm it on the dashboard: the
   service `Settings` pane (`Deploy` section) and the deployment detail page, which
   shows a file icon for any value that came from config in code.
2. **Is the healthcheck port right?** Railway probes the injected `PORT`. The API
   image pins `ASPNETCORE_HTTP_PORTS=8080` (`apps/api/Dockerfile:10`, identical on `1e2cb80a` and `bea15310`; the first draft cited `:19`, which was wrong at both) and does not
   read `PORT`. If the service port and the probe port disagree, Railway reports
   `service unavailable`. Confirm `PORT=8080` on the API service, or record the
   target-port setting that makes them agree.
3. **Who produced the 3 requests?** Only the request pattern distinguishes a platform
   probe, the release smoke, and browser traffic. Compare `requestId`/`X-Request-ID`
   in the deployment logs against the reviewer's smoke run.

### 1.4 Current-head qualification and hold (2026-11-26)

This subsection separates readiness-tool preparation from permission to cut over.
The prior current-head record is [PR comment #7203105747](https://github.com/rebin2002/TaslimAI/pull/1277#issuecomment-7203105747), recorded against PR head
`ff65507b7ef5ae502fe5972843e2591753282c0f` and base `54bb501bd4dad690d81eb7bbe805c69dc8e37c70`.
At this qualification, live `main` is
`26f8e3e74f2a1b7c287dce8ad548229a33ed16a2`, which includes #1292, #1294, and #1298.
The prior `61a8c781…` snapshot was superseded while this work was in progress; the
new head was fetched and merged append-only. The three-way merge is clean and the PR
source diff is exactly `.github/workflows/ci.yml`, this document, and the two
`scripts/deploy-readiness-gate*` files.

The separately owned [#1144](https://github.com/rebin2002/TaslimAI/pull/1144) remains
open for review on `7afa4ac936e7562a9fea2d72dcdf76c0d1831ba0`. Its CI workflow patch
also applies cleanly to current `main`; its branch and changes remain separate and
untouched. The #1277 smoke-contract step stays anchored immediately after checkout;
it does not overwrite the independently reviewed FFmpeg setup and real-media steps
owned by #1144.

**Hosted API test hold.** Job
[`140157463873`](https://github.com/rebin2002/TaslimAI/actions/runs/45890269180/job/140157463873)
ran the broad integration suite on merge candidate
`9d3e77742ae6302bda3d5b63ef184a7ec45b3287` (PR head `ff65507…`, base
`54bb501…`): 3,649 passed and 2 failed. The two failures were actual HTTP 500
responses from `GenerationJobConcurrencyIdempotencyTests`:
`Simultaneous_same_key_with_conflicting_payloads_never_double_queues` and
`Simultaneous_terminal_retries_with_one_key_fan_into_one_child_with_one_reservation`.
Both use the worker-disabled `GenerationJobsApiFactory` ordinary HTTP fixture backed
by SQLite, not the hosted PostgreSQL service. The xUnit failure retained only the
truncated `500 ... INTERNAL_ERROR` body and assertion stack; it did not retain the
server exception or a correlated request log. The test fixture creates a per-factory
SQLite file, enables WAL, replaces the API database context with `UseSqlite`, and
disables its job worker (`apps/api.Tests/GenerationJobsTests.cs` and
`GenerationJobConcurrencyIdempotencyTests.cs`). The test assembly also disables
xUnit class parallelism (`apps/api.Tests/IdentityProjectsTests.cs`); only the
requests inside each race are concurrent.

The later PostgreSQL service messages are attributable to separate, deliberately
exercised tests: the `GenerationJobs.ConcurrencyToken` lock timeout is the claim-fence
test `Claim_takeover_serializes_behind_open_fence_and_stale_worker_cannot_stage_postgres`
in `ResearchSourceLifecycleTests.cs`; the `MovieStoryRevisions` duplicate key is the
expected losing writer in `MovieStoryRevisionAllocationConflictTests.cs` (the #1292
barrier makes that allocation collision deterministic); and the `Title` length error
is asserted by `The_unbounded_manifest_label_is_refused_by_the_job_title_column` in
`MovieExportManifestSemanticsTests.cs`. All three messages are later than the two
request failures and cannot explain the SQLite responses.

Both request tests were run individually against fetched current `main`
(`26f8e3e…`, with #1292, #1294, and #1298 present), using that isolated
SQLite/WebApplicationFactory fixture; each passed **1/1**. #1298 moves the same-key
precheck earlier in `CreateAsync` and atomically links MovieAssembly publication;
the latter path is not exercised by these `SystemTest` jobs. #1292 changes Movie
revision and Chat cancellation fixtures; #1294 serializes provider completion claims.
These isolated passes do not explain the hosted full-suite 500s or clear the still-red
hosted API integration check. Do not label the failures flaky or resolved until a
correlated exception or an exact-head hosted API run establishes that. A review lead,
not a finding, is that `GenerationJobService.CreateAsync` still starts its
transaction before the `try` and catches only `DbUpdateException` in its same-key
conflict recovery path; a SQLite contention exception escaping that boundary could
produce a 500, but no captured exception ties this path to the hosted responses.
This PR makes no application-code change.

**Readiness rehearsal.** The shell contract suite uses loopback-only HTTP fixtures:
ordinary healthy API/Web responses, an initial API proxy 502 followed by a healthy
retry, required-dependency red, and matching/mismatching/unavailable revision
identity. The local two-service candidate fixture supplies the expected revision in
the API and Web response headers. This rehearses retry and identity behavior; it is
not a deployed staging environment and does not establish platform configuration.
On the current #1277 worktree, `bash -n scripts/deploy-readiness-gate.sh
scripts/deploy-readiness-gate.test.sh` and `git diff --check` pass, and
`bash scripts/deploy-readiness-gate.test.sh` reports **70 passed / 0 failed**.

**Remaining cutover holds.** The readiness code and its loopback contract can be
reviewed independently of platform changes, but the hosted API integration failure
and independent-review requirement remain open. Actual isolated Railway staging has
not been exercised; no service settings were changed. Production healthcheck values,
staged no-op deployment and deployment-scoped logs must still be verified in a
separate environment before any production apply. The shared production service
domain exposes no candidate revision identity, so an unverified green remains
shape-only and cannot authorize promotion. No Railway action or production probe is
recorded by this qualification.

## 2. The gate contract

`deploy-readiness-gate.sh` promotes a cutover only when both services are
demonstrably serving. Its verdict taxonomy is deliberate: an application 503 and a
proxy 502 mean opposite things and must not be merged into one failure.

**The gate and the platform healthcheck deliberately use different API
endpoints.** This script is an *operator* decision aid and reads `/health/ready`,
because a human benefits from seeing which dependency is red and why. The
automatic promotion gate in section 3.2 is recommended as `/health/live`, because a
machine that blocks promotion on a storage blip converts an observability gap into
an outage. Reading these two as one decision is the mistake to avoid; the split and
its reason are recorded in section 3.2.

| Verdict | Meaning | Promote? |
| --- | --- | --- |
| `ready` | 2xx and every `required: true` check is `available` | yes |
| `not-ready` | bound, but a required dependency check is red | no; investigate the dependency |
| `no-upstream` | refused / timeout / empty reply, or a 5xx carrying no Taslim readiness document | no; the revision never bound |
| `throttled` | 429 from the health limiter | no; back the probe rate off |
| `redirect` | 3xx. Reached only because the probe **does not follow redirects**: following one lands on a 200 page and would be classified green | no; a redirect is not a serving endpoint |
| `foreign-service` | a 2xx readiness document whose `service` is not the expected one | no; wrong URL, wrong vhost, or a proxy answering with another app's JSON |
| `conflict-5xx` | a 5xx whose own body claims `ready` | no; self-contradictory, and no non-2xx is ever promotable |
| `identity-mismatch` | a revision is demanded and the responder names a different one | no; the replacement is not serving |
| `identity-unavailable` | a revision is demanded but the responder exposes no revision field or header | no; proof was asked for and cannot be produced - see section 2.1 |
| `http-N`, `bad-body` | route or contract drift | no; inspect before retrying |

`GATE_EXPECT_SERVICE` defaults to `Taslim API` and is what makes the
`foreign-service` check possible; setting it empty opts out deliberately.
`GATE_EXPECT_REVISION` is empty by default, which is why the normal PASS prints
`IDENTITY UNVERIFIED` instead of claiming authority (section 2.1).

**Malformed documents are classified, not crashed on.** This was checked by
feeding the gate deliberately broken readiness responses on loopback. At the
reviewed head, a JSON *array* instead of an object, or a `checks` entry that is a
bare string rather than an object, raised an unhandled exception inside the
classifier and printed `probe-error` - a red, but with no diagnosis, and an
exception path in a tool meant to be read under pressure. Both now resolve to a
named verdict (`bad-body` and `not-ready` respectively). A `204` with no body and
a `503` carrying HTML are `bad-body` and `no-upstream`; a `500` is `http-500`,
which is deliberately *not* the same thing as a `503` with our document. Only a
genuine 2xx readiness document with an available required check produces `ready`.

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

### 2.1 What the gate is NOT: promotion authority without identity

**A green verdict is not proof that the replacement revision is serving.** The
platform routes a *service* domain to whichever deployment is currently active, so
during a cutover the previous revision can answer every check in this gate and
produce a green. The gate's honest claim is narrower than its name:

> an endpoint of the expected shape and identity is answering at this URL

not

> the replacement revision is answering.

Confirmed against the shipped response at `1e2cb80a`, sampled live at
`2026-11-24T19:34:39Z`: the readiness document carries only `status`, `service`,
`requestId` and `checks`, and the headers carry only per-request identifiers
(`x-request-id`, `x-railway-request-id`) plus platform routing metadata
(`x-railway-edge`, `x-hikari-trace`). **No revision, build SHA or deployment id is
observable from outside the container**, so there is currently nothing to compare.

The gate behaves as follows rather than pretending otherwise:

| `GATE_EXPECT_REVISION` | Behaviour |
| --- | --- |
| unset (default) | green is green, but the PASS line prints `PASS (IDENTITY UNVERIFIED)` and states that this is **not a promotion authority** |
| set, responder exposes a revision | compared against `revision`/`buildSha`/`gitCommit`/`version` in the body, or `x-taslim-revision`/`x-revision`/`x-build-sha`/`x-git-sha`/`x-commit-sha` in the headers. Match → `revision identity verified`; mismatch → RED `identity-mismatch` |
| set, responder exposes no identity at all | **RED `identity-unavailable`**, not a green. Demanding proof and not receiving it is a failure, because the alternative is the false green described above |

Two consequences for how this is used:

1. **Against the public service domain it is a shape assertion only.** The
   review's remedy - point the gate at a *deployment-scoped* URL - is **not
   available on this platform**, and this lane corrected it rather than repeating
   it: Railway's routing granularity is environment and service, never a single
   deployment id (https://docs.railway.com/environments; domains in PR
   environments are provisioned per *service*, and only when the base environment's
   service already has a Railway domain). There is no documented domain that pins
   to one deployment. What *is* achievable:
   - **identity by isolation** - a staging or PR environment is its own
     environment+service, so its domain has exactly one candidate to serve. A green
     there is a promotion assertion; a green on the shared production domain is not.
   - **identity by disclosure** - land a revision field or header and compare it,
     which is what `GATE_EXPECT_REVISION` already consumes.
2. **Responder identity is checked on the green path, not only on the error path.**
   A foreign responder wearing the readiness shape is `foreign-service` and blocks.
   `service == "Taslim API"` is the only identity available today, and it is now
   pinned by tests.

Making revision identity provable needs an application change this lane
deliberately does not make: adding `revision`/`buildSha` to
`OperationalHealthResponse`, or exposing `/version`, edits
`apps/api/Infrastructure/OperationalObservability.cs` - the file lane **#1028** is
already editing for the advisory `worker` check - so it belongs in a follow-up once
#1028 has landed, not in this diff. The gate is already built to consume that field
the moment it exists; nothing here has to change to accept it.

## 3. Configuration change

### 3.1 Do not add `railway.json`

Railway **Config as Code** (`railway.json` / `railway.toml`) is deprecated and its
files stop being read on **2026-12-01** (six days after this re-verification on
`2026-11-25`; the first draft's “a week from this writing” was left to drift); new services
cannot opt into it. Adding one now would buy one deployment of durability and then
silently stop applying. The supported vehicle is **Infrastructure as Code**,
`.railway/railway.ts`, evaluated by the CLI as `plan` then `apply`.

The immediate remedy therefore needs **no repository change at all**: it is two
dashboard values.

### 3.2 Exact settings to apply, and their values

Applied **one service at a time**, in this order, each verified before the next.
Values are reconciled with the independent review in #7141321366 section 5; where
that review and the first draft of this document disagreed, the disagreement and
its resolution are recorded immediately below the table rather than quietly
adopted.

| Order | Service | Setting | Value | Why |
| --- | --- | --- | --- | --- |
| 1 | Taslim Web | Healthcheck Path | `/` | public, non-paid, matches the existing smoke contract |
| 1 | Taslim Web | Healthcheck Timeout | `30` | `next start` binds without a migration chain |
| 2 | Taslim API | Healthcheck Path | **`/health/live`** | see the reversal below |
| 2 | Taslim API | Healthcheck Timeout | **`60` minimum** | measured cold window is 21.6s worst case; the reviewer's floor of 60s leaves ~3x margin plus image start |
| 2 | Taslim API | Restart policy | `ON_FAILURE`, max retries `10` | unchanged from the platform default; a failed healthcheck marks the deploy failed, it does not restart a healthy process |
| both | Overlap seconds | `30` | keeps the previous deployment answering while clients finish in-flight work |
| both | Draining seconds | `10` | graceful SIGTERM window; the readiness endpoint already returns 503 while stopping |
| both | `targetPort` | `8080`, **unchanged** | the API image pins `ASPNETCORE_HTTP_PORTS=8080`; do not "fix" the port by editing the image during a cutover |

**Reversal of the first draft, and why it is correct.** This section originally
specified `/health/ready` for the API, reasoning that promotion should be gated on
dependencies rather than on a bare process. The review's objection (#7141321366
section 4, items 1 and 5) is sound and this draft accepts it:
`Health:StorageRequired=true` makes an R2 blip a *required* readiness failure, and
the platform will not promote on anything but a 2xx. A strict gate can therefore
block a perfectly good revision and stall the deploy - "a self-inflicted outage
disguised as a safety measure". The defect observed in #1245 is a container not
*bound*, which liveness proves exactly and nothing else. So:

- the **platform** gate is `/health/live` - bind proof, no dependency can block it;
- the **operator** gate stays `/health/ready` - dependency detail, read by a human,
  which is where a red storage check is information rather than an outage.

That split keeps the property this lane was built for without importing the
blocking risk. If `/health/ready` is ever preferred for automatic promotion,
`Health:StorageRequired` must be pinned `false` first, or the probe must tolerate
advisory red - and either change is a configuration mutation with a blast radius,
not a documentation edit.

**Never flip both services in one apply.** One service, one redeploy, one
observation, then the other: promoting Web to a revision whose API contract differs
while the API is held unhealthy produces a mixed fleet.

Equivalent IaC for when the project is migrated deliberately. This file is **not**
added by this lane, because `railway config apply` takes ownership of the whole
project graph and must not be attempted blind - and because one service cannot be
managed by Config as Code and IaC at the same time:

```ts
import { defineRailway, project, service } from "railway/iac";

export default defineRailway(() => project("taslim", {
  resources: [
    service("Taslim Web", { healthcheck: "/", healthcheckTimeout: 30 }),
    service("Taslim API", { healthcheck: "/health/live", healthcheckTimeout: 60 }),
  ],
}));
```

### 3.3 Verify in a staged environment first

No production setting is changed by anything in this repository. Verify before
promoting, on a **separate Railway environment or a throwaway project**, never on
the production environment:

**Staging comes before any production apply, in every variant of this plan,
including the config-only one.** A setting that has never been observed to change
an outcome has not been verified, whatever the documentation says.

1. **Snapshot first.** Record the current Web and API service config - env names,
   start command, port, health fields - to a dated file *before* changing anything.
   This is what makes each step below reversible by config rather than by
   redeploying old code.
2. `railway link` to the staged project, then `railway config pull --force` and
   inspect it, so the staged graph is known rather than assumed.
3. **Web only:** set `healthcheckPath=/`, `healthcheckTimeout=30`. Lowest-risk
   service, no migration step. Trigger no deploy yet.
4. **API only, after Web is observed:** set `healthcheckPath=/health/live`,
   `healthcheckTimeout=60` (never less), and keep the platform probe interval at or
   above 30s so it cannot consume the #249 anonymous budget or throttle itself.
5. Deploy the candidate SHA to staging and read the deployment detail page: the
   healthcheck value must show, with the icon that names its config source.
6. **Validate with a staged no-op deploy of the API alone** - redeploy the same
   revision - and confirm promotion now waits for the bound listener: the
   #1245-class burst of 502s on `/health` must be absent from the deployment-scoped
   logs. Only after that observation is the same done for Web.
7. Confirm the gate **can go red**, which is the check that distinguishes a gate
   from a formality: point the operator gate at a staged API with an unreachable
   database and it must exit 1 with `not-ready`; the platform deploy must not reach
   `SUCCESS` before its timeout.
8. Confirm the operator gate's own PASS text is read, not just its exit code -
   against staging it should say `IDENTITY UNVERIFIED` while the API exposes no
   revision field (section 2.1). That message is expected here and is not a defect.
9. **Only then** apply the same values to production, in the same one-service-per-
   apply order, each followed by its own observation.

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
   services. If they are unset, set them before the release rather than during it,
   and **one service per apply** - never both at once.
3. Merge through the normal reviewed path. Each service then deploys independently:
   Railway applies no startup ordering to GitHub push deploys, so Web may switch
   while the API is still migrating. The gate in step 4 is what closes that gap.
4. Immediately after the API deployment leaves `DEPLOYING`, run the gate and read
   the **verdicts and their wording**, not just the exit code and not the
   deployment status. Expect `IDENTITY UNVERIFIED` here: on the production service
   domain the platform can only route to "whichever deployment of this service is
   active", so a green is a shape assertion, not proof the replacement is serving
   (section 2.1). A green that *is* a promotion assertion has to come from an
   isolated environment, or from a revision field the API does not yet expose.

   ```bash
   # against the candidate in its own environment: identity by isolation
   bash scripts/deploy-readiness-gate.sh "$STAGED_API_URL" "$STAGED_WEB_URL" 300

   # against production: shape assertion only - the PASS line says so explicitly
   bash scripts/deploy-readiness-gate.sh \
     https://taslim-api-production.up.railway.app \
     https://taslim-web-production.up.railway.app 300
   ```

   Measured at `2026-11-24T20:12Z` against live production, both forms behave as
   documented: the default run returned `PASS (IDENTITY UNVERIFIED)` at exit 0,
   and `GATE_EXPECT_REVISION=1e2cb80a` returned `identity-unavailable` at exit 1
   for **both** services - a correct refusal, not a failed deploy. The same demand
   against a responder that does publish a revision returned `ready` on a matching
   prefix and `identity-mismatch` on a non-matching one, so the mechanism
   discriminates; it simply has nothing to read from this API yet.

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
| `foreign-service` | something that is not the Taslim API answered the readiness path | wrong URL, wrong vhost, or a proxy fronting another app. Nothing to roll back; fix the target the gate was pointed at |
| `conflict-5xx` | a 5xx carrying a document that claims `ready` | a non-2xx is never promotable. Treat as not bound and investigate the proxy, not the app |
| `identity-mismatch` | the responder identifies a revision other than the expected one | **the replacement is not serving.** Do not promote. Check which deployment the domain is routed to; the previous revision is still live |
| `identity-unavailable` | identity was demanded but the responder exposes no revision field or header | expected against the shared production domain until a revision field or `/version` lands - the API exposes none today, so this verdict is the gate behaving correctly, not a failed deploy. Re-run the gate **pointed at an isolated staging or PR environment** (one candidate to serve, so a green there is a promotion assertion), or read the production green as shape-only. There is **no per-deployment URL** to fall back on: Railway routes at environment and service granularity (section 2.1). Deployment **logs** are per-deployment, which is how you confirm which one started |
| gate green but the release is wrong | a functional regression, not a cutover failure | roll back. Note that a green on the *service domain* does not prove the new revision is the one answering (section 2.1), so confirm the serving revision before concluding a regression is attributable to it |

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
| **#249** (open, `MERGEABLE`, base `d3fffca5`) | health rate limit: 2 concurrent and 30/min per client IP on `/health`, `/health/live`, `/health/ready`, `/readiness` | **live interaction, and the budget is per client IP - which matters for who it actually constrains.** The gate is operator-initiated, so it shares the *operator's* allowance: default retry 5s = 12/min per service, 24/min for the pair, with a distinct `throttled` verdict. Platform healthcheck probes come from Railway's own source address, not the operator's, so they do not consume the operator's budget - but they can exhaust **their own** if the interval is tight, which is why section 3.2 sets the API probe interval at 30s or above. CI smoke runs from GitHub runners, a third address. Three independent budgets, not one shared pool |
| **#7141321366** (independent technical review of this PR's head) | found the identity gap and four unpinned classification edges; recommended the Web-then-API `/health/live` sequence | **accepted, and it changed this document.** Sections 2.1, 3.2 and 3.3 are the reconciliation. One point is refined rather than adopted: the review's item 4 treats platform probes, the operator gate and CI smoke as sharing one anonymous allowance, but the limit is keyed per client IP, so they contend separately - see the #249 row above. The review classifies all four gaps as test-coverage only; **three were also live false greens at the shipped head**, verified by running that head's script against the same responders, and one (MUT-D) is coverage-only exactly as reported - section 7.1 |
| **#858 / #1143 / #1146 / #1147** (four open CI lanes, none owned by a running task; #1143 is the current-main re-cut of #858) | each appends its own steps to the `smoke-contracts` job plus a top-level `concurrency` block | **the collision this revision resolves.** Measured with real three-way merges, not inferred: against head `28946450` each of these four lanes conflicted in `.github/workflows/ci.yml` **in both landing orders**, because all of them insert at the same tail anchor — the end of the `smoke-contracts` step list. The first draft's note that “merge ordering may need a re-cut” was therefore wrong: no ordering avoids a shared insertion point. The step is now anchored directly after `actions/checkout`, four unchanged lines clear of the contested tail, with every contract step preserved verbatim. §7.4 records the merge matrix and the combined-tree proof. |
| **#66** (open, non-draft, **`MERGEABLE`/`clean`** at head `82e37ca173`, last updated 2026-11-24T22:39Z; the first draft recorded it as `CONFLICTING` against a 2026-10-05 base, which is no longer true) | bounds startup migration time via `Database:MigrationTimeoutSeconds` | **not duplicated.** Measurement B shows the window exists without migrations, so bounding migration time is necessary-but-not-sufficient. #66 edits `Program.cs`, `README.md`, `docs/OPERATIONS_RUNBOOK.md` and one `dotnet run --urls` line in `ci.yml` around line 135; that `ci.yml` hunk does **not** touch the `smoke-contracts` step list, so it never collided with this lane. Its `Program.cs` edits do move the line numbers cited in §1.2, which is why those citations now name the head they were measured on |
| #726, #284 | staging rollback contract tests, release-governance evidence | rollback steps in section 5 are the operational counterpart; no test-lane files touched |
| #258, #461 | readiness redaction, provider failure status | untouched |

File footprint of this lane: `scripts/deploy-readiness-gate.sh`,
`scripts/deploy-readiness-gate.test.sh`, one added step in the `smoke-contracts` job
of `.github/workflows/ci.yml`, and this document. No `.cs`, no `appsettings*`, no
Dockerfile, no `Program.cs`, no `README.md`, no `docs/OPERATIONS_RUNBOOK.md`, so the
payload is disjoint from #1028, #249 and #66 file-for-file. The one shared file is
`ci.yml`, and the collision it caused is resolved by relocating the anchor, never by
removing a check: position inside the job changes *where* the step runs, not *whether*
it runs or gates. A scan of all 354 open PRs, each diffed against its own merge-base,
found no other lane touching `docs/DEPLOYMENT_CUTOVER.md` or either
`scripts/deploy-readiness-gate*` file.

## 7. Verification performed

Local, offline, from `1e2cb80a`; no production resource and no paid provider call
was touched.

- `bash -n scripts/deploy-readiness-gate.sh` - clean.
- `bash scripts/deploy-readiness-gate.test.sh` - **55 passed, 0 failed** (18 at the
  first head, grown to 43, 48, then 55 in response to review `7141321366`). The suite
  runs the real script against loopback HTTP responders serving hand-written bodies,
  so every verdict is exercised end to end rather than by mocking: green promotion,
  advisory degradation staying green, a red required check blocking, empty and
  advisory-only `checks`, a missing `checks` array, a `checks` entry that is a bare
  string, a JSON array body, proxy 502 versus application 503 disambiguation, a 5xx
  whose body falsely claims `ready`, a foreign responder wearing the readiness shape,
  301/302 refusal, 429 handling, demanded-revision match and mismatch and absence,
  Web failing independently of a green API, a closed port reporting `no-upstream`,
  API-only operation, the 5s probe-interval default, and the usage guards.

### 7.1 Three of the four review gaps were live false greens, not test gaps

`7141321366` classifies all four gaps as surviving mutants - a statement about the
*tests*. Three of them were also producing a wrong verdict at the shipped head
`c0156e2e`, and one was not. Rather than reason about it from the source, the old
script (`git show c0156e2e:...`) and the new one were run **side by side against the
same loopback responders**:

| Responder | Old script at `c0156e2e` | New script | |
| --- | --- | --- | --- |
| 200, valid shape, `"service":"Not The Taslim API"` (MUT-E) | API `GREEN`, overall exit **0** | `foreign-service`, exit 1 | **live false green** |
| 503 (or 502) whose own body says `service:"Taslim API", status:"ready"` (MUT-G) | API `GREEN`, overall exit **0** | `conflict-5xx`, exit 1 | **live false green** |
| Web 302 `/` -> 200 sign-in page, API honest (MUT-F) | overall exit **0**, printed `PASS - readiness-gated cutover is clear for both services` | `redirect`, exit 1 | **live false green** |
| `checks: []`, and an advisory-only `checks` list (MUT-D) | exit 1 (red) | exit 1 (red) | **coverage-only**, as the review said |
| 502 with an HTML body | `no-upstream`, exit 1 | `no-upstream`, exit 1 | unchanged, correct |
| 200 with a JSON *array* body, and `checks: ["database"]` | `probe-error`, exit 1, no diagnosis | `bad-body`, exit 1 | red-but-undiagnosed, fixed |

So MUT-D is fairly reported as a coverage gap: an earlier draft merged the
malformed-document and empty-check-set decisions into one guard
(`if not checks or not required`), which made the shipped behaviour correct **by
luck of the neighbouring clause** while leaving the `or not required` half unpinnable
by any test. Removing that clause does not go green today; it would if the sibling
clause ever moved, which is the actual fragility. The guards are now one decision each.

The 3xx case was unreachable code: `probe()` passed `curl --location`, so a 302 was
followed to its 200 destination before the classifier ever saw the redirect status.
`--max-redirs 0` now makes the branch live. A Web service behind a sign-in redirect
would have printed the PASS line. That is why `redirect` is a verdict and not a note.

Also corrected from this lane's own earlier work: `GATE_RETRY_SECONDS` shipped a
default of `2` while its comment and the phase-1 release record both said `5`. Two
seconds is 30 probes/min per service, at or over #249's anonymous budget. The default
is now `5` and a test pins it, because the review's finding is fair: the suite did not
previously fail if a guard was removed.

### 7.2 Mutation check - is each guard individually necessary?

Each classifier guard was removed one at a time from a throwaway copy and the suite
re-run; a mutant that still passes means the suite does not pin that protection. All
thirteen specs are now killed, including the four reported by the review:

| Mutant | Result |
| --- | --- |
| MUT-D drop the no-required-checks clause | killed |
| MUT-E drop responder identity on the green path | killed |
| MUT-F classify 3xx as `ready` | killed |
| MUT-G classify an app 5xx as `ready` | killed |
| NEW-A drop the malformed-document guard | killed |
| NEW-B drop the never-ready-on-5xx guard | killed |
| NEW-C follow redirects again | killed |
| NEW-D drop the demanded-identity comparison | killed |
| NEW-E drop the status-word check | killed |
| NEW-F drop the red-required-check scan | killed |
| NEW-G drop the shutdown-drain 503 classification | killed |
| NEW-H silence the `IDENTITY UNVERIFIED` warning | killed |
| NEW-I revert the probe interval to 2s | killed |

Result: **13 killed, 0 survived, 0 patterns that failed to apply.** Two details worth
recording, because both are places where a mutation run can lie:

- The first pass of this harness killed nine and left three alive (`NEW-E` the
  status-word check, `NEW-G` the gateway-versus-dependency split on a 5xx, `NEW-H` the
  identity banner's explanatory line). Those three are what the last seven assertions
  were written for, and `NEW-D` initially reported **not applied** rather than killed:
  its pattern matched two places, because the readiness path and the Web path each
  demand identity separately. Disambiguating the pattern made it a real mutant, and it
  dies on five assertions. A "not applied" line is not a pass.
- `NEW-I` (reverting the probe interval to 2s) is **not** caught by the ordinary
  assertions, because every other scenario overrides `GATE_RETRY_SECONDS` to 0.2s to
  keep the suite fast - so the shipped default is invisible to them, and a suite that
  reports "all green" here would be lying about that guard. It is pinned instead by one
  assertion that invokes the gate **without** overriding the variable and requires the
  banner to read `budget 1s, retry 5s`.

The harness itself is a local verification artefact, deliberately not committed, since
it exists only to answer a review claim about this script.

### 7.3 Live verification, read-only

- `deploy-readiness-gate.sh` against production, `20:12Z`: default run printed
  `PASS (IDENTITY UNVERIFIED)` and exited 0. Attempt 1 on the API returned a transient
  `no-upstream` and attempt 2 was green 10s later; that is one sample of the instability
  the retry design exists for, not a reason to loosen any timeout.
- With `GATE_EXPECT_REVISION=1e2cb80a`: **both** services returned `identity-unavailable`
  and exit 1. The gate refuses to claim a revision it cannot observe.
- Against a synthetic responder that does publish a revision: matching prefix printed
  `ready`, a non-matching one printed `identity-mismatch`. The mechanism works; the
  production API simply does not expose the field yet (section 2.1).
- Response headers on the live health routes carry `x-request-id`,
  `x-railway-request-id`, `x-railway-edge` and `x-hikari-trace` - no revision, build or
  deployment identifier. This is the observed basis for the identity limitation, not an
  inference from the response schema alone.

Existing contracts were re-run and still pass: `scripts/api-health-smoke.test.sh`,
`scripts/container-health-smoke.test.sh`, `scripts/web-container-smoke.test.sh`,
`scripts/readme-health-contract.test.sh`. The startup window was measured three ways
against PostgreSQL 16 on loopback (section 1.2).

Not done, and why: the container smoke scripts require the Docker CLI, absent from
this sandbox, and Railway project access would be a production mutation. Both are
recorded as preconditions in section 3.3 rather than assumed green.

Hosted CI history on this branch, checked per SHA rather than assumed to carry over:

- Head `6b6897b9` (runs 45454549093 and 45454549235) - **6 of 7 `success`, none
  failed**; `Deployment smoke helper contracts` step log showed the gate suite at
  `18 passed, 0 failed` on the runner, and `Browser E2E` was still `in_progress` at
  that reading, with its `api-health-smoke` step already logging `PASS`.
- Head `4ba60ca8` - **7 of 7 `success`**, `Browser E2E` included, having finished
  after the earlier reading. The `Deployment smoke helper contracts` runner log for
  that head (run 45478032545, job 138743357628) confirms the suite ran there and
  reported `48 passed, 0 failed` - so the contract tests execute on the hosted
  runner, not only in this sandbox.
- `Build production images and smoke-test API` is the Docker-backed job, so
  `container-health-smoke` and `web-container-smoke` are green **on the runner against
  the real production images**, which is the one class of verification this sandbox
  cannot perform.

The head carrying this document and the 55-test suite is re-verified on the hosted
runners before the reconciliation comment is posted on #1277, and the check list at
that exact SHA is recorded there rather than being asserted here in advance.

Still not done by this lane, deliberately: any `railway config plan` / `apply`, and
applying the section 3.2 settings to any environment. Those are live changes and are
the release owner's call, gated on section 3.3.

### 7.4 Re-verification against live main `bea15310` (2026-11-25)

The measurements above were taken at `1e2cb80a`. `main` has since advanced 43 commits
to `bea15310cdb4a495ef1c47456eaa349d4d3af604` (merged #1268, 2026-11-25T00:32:37Z), so
this section re-measures what the earlier sections assert, and states plainly what
could not be re-measured here.

**Uplift.** `origin/main` was merged into this branch append-only (`git merge --no-ff`,
no rebase, no force-push, no conflict resolution). The branch went from 5 ahead / 43
behind to **0 behind**, and the merge was clean: `main` touched none of this lane's four
files since `1e2cb80a`.

**The CI collision, measured.** `git merge-tree` and real scratch three-way merges at
the pre-resolution head `28946450`:

| Pairing | Before relocation | After relocation |
| --- | --- | --- |
| main + #1277 alone | clean | clean |
| main + #1143 alone | clean | n/a |
| #1143 then #1277 | **CONFLICT** `ci.yml` | clean |
| #1277 then #1143 | **CONFLICT** `ci.yml` | clean |
| #858 either order | **CONFLICT** `ci.yml` | clean |
| #1146 either order | **CONFLICT** `ci.yml` | clean |
| #1147 either order | **CONFLICT** `ci.yml` | clean |
| #1144, #298, #66 either order | clean | clean |

Conflict direction was symmetric, which is what rules out the "re-cut in the right
order" reading: two lanes inserting at the same point conflict whichever lands first.

**Nothing was weakened to get there.** The combined tree of `main` + #1143 + this branch
parses as YAML, keeps the `concurrency:` block, and carries all four `smoke-contracts`
steps (`Validate deployment readiness cutover gate`, `Validate API health smoke helper`,
`Validate production container smoke helpers`, `Enforce deploy-safe CI/release
concurrency policy`). The gate count is unchanged at five `ci.yml` jobs plus container
smoke plus release evidence. The only behavioural difference is precedence: the
readiness contract now runs before the API health contract instead of after it, so a
regression in one masks the other's signal in the same job, exactly as it did in the
opposite direction before. Neither ordering enforces more checks than the other.

**Local suites on the uplifted tree**, offline, loopback only:
`deploy-readiness-gate.test.sh` **55 passed / 0 failed**; `api-health-smoke.test.sh`,
`container-health-smoke.test.sh`, `web-container-smoke.test.sh`,
`release-evidence.test.sh`, `readme-health-contract.test.sh` and `next-wave-refs.test.sh`
(15/15) all exit 0. `bash -n` clean; `git diff --check` clean; `ci.yml` parses.

**Facts that moved and are now corrected in this document:**

| Claim | At `1e2cb80a` | At `bea15310` |
| --- | --- | --- |
| EF migration chain | 82, head `20261005112000_AddImageActiveJobGuard` | **83**, head `20261124065935_AddAcademicResearchIntegration` |
| `Program.cs` health routes cited | `567-599` | **`586-608`**; migration apply `610-615`, `app.Run()` `621` |
| `apps/api/Dockerfile` port line | cited as `:19` | **`:10`** — wrong at both heads, a citation defect not a drift |
| #66 merge state | recorded `CONFLICTING` | **`MERGEABLE`/`clean`** at `82e37ca173` |
| Revision identity in the readiness body | absent | **still absent**: `OperationalHealthResponse` remains `Status, Service, RequestId, Checks`, and #1028 is still open at `6f6edb9a04`, so §2.1's `identity-unavailable` refusal is still the correct behaviour |
| `railway.json` / `railway.toml` / `.railway/` in the tree | none | **still none**, so health-check settings remain dashboard-only and unreviewable |
| Branch protection / rulesets | none | **still none**: `404` on `/branches/main/protection`, `[]` from `/rulesets`, #283 still open |

**Not re-measured here, stated as a limit rather than assumed.** The 8.1 s / 7.6 s /
21.6 s startup windows of §1.2 were **not** reproduced: this sandbox has no .NET SDK, no
PostgreSQL and no Docker, so no bind-window figure on `bea15310` is claimed anywhere in
this document. Because the chain is one migration longer, 21.6 s is a **floor**, and the
60 s API healthcheck floor in §3.2 should be re-measured against the 83-migration chain
before it is relied on. Container-backed smoke remains a hosted-runner verification.
