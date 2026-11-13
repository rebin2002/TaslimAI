# PostgreSQL 18 parity lane (issue #734)

Production runs PostgreSQL 18 (`ghcr.io/railwayapp-templates/postgres-ssl:18`),
while every database-backed job in `.github/workflows/ci.yml` runs against a
SHA-pinned **PostgreSQL 15** image. A green `ci.yml` run therefore proves
PG15 compatibility, not PG18 compatibility. This lane closes that gap as a
test-environment parity requirement. It is not evidence of a production
PostgreSQL failure, and it does not qualify a release on its own.

## Ownership boundary

This lane deliberately touches **no file claimed by another owner**:

| Path | Owner status |
| --- | --- |
| `.github/workflows/pg18-parity.yml` | new file, owned by this lane |
| `scripts/pg18-parity-gate.sh` | new file, owned by this lane |
| `scripts/pg18-parity-gate.test.sh` | new file, owned by this lane |
| `scripts/lib/pg18-parity-refs.sh` | new file, owned by this lane |
| `.github/workflows/ci.yml` | **untouched** - `#109` owns the `autopilot-gate.if` hunk, `#66` owns the Browser E2E hunk |
| `scripts/lib/next-wave-refs.sh`, `scripts/next-wave-refs.test.sh` | **untouched** - owned by `#297` |

Nothing here edits the shared workflow, the existing exact-ref gate, or any
product source. Merging `.github/workflows/ci.yml` changes remains that file
owner's decision; this lane exists so an executed PG18 result can be produced
without waiting for it.

## Safe defaults

* The workflow is **`workflow_dispatch` only**. There is no `push`,
  `pull_request`, or `schedule` trigger, so an ordinary branch or PR cannot
  consume CI minutes or silently start a database-backed run.
* `legs` defaults to `guards-only`, which runs the offline guard contracts and
  touches no database at all.
* `candidate_ref` lets the lane qualify an **exact composed candidate** (for
  example `#718` + `#720`) without merging anything.
* Every database the lane can reach must use the disposable `taslim_pg18_*`
  prefix on a loopback/service host. An inherited production URL is refused
  before any connection is opened, so "no migrations on real databases" is
  enforced by the harness, not by convention.
* The expected PostgreSQL major is read from the live server. A run whose
  server is not the expected major fails, which is what prevents a green PG15
  execution from ever being reported as PG18 qualification.
* `Ai__OpenAI__Enabled` and `MovieVideo__Enabled` are forced off for the whole
  run: provider-free, fake Manus/Bridge only, no deploy, no merge, no charging.

## Modes

```bash
bash scripts/pg18-parity-gate.test.sh          # offline guard contracts, no DB
bash scripts/pg18-parity-gate.sh autopilot            # PG18: Autopilot + ladder
bash scripts/pg18-parity-gate.sh billing             # PG18: billing/ledger + ladder
bash scripts/pg18-parity-gate.sh autopilot-control   # PG15 control, same filter
bash scripts/pg18-parity-gate.sh billing-control     # PG15 control, same filter
bash scripts/pg18-parity-gate.sh schema-diff         # PG15 vs PG18 schema parity
```

A local run requires the typed switch `PG18_PARITY_LOCAL_OPT_IN=1`; without it
the harness refuses. The migration ladder and the test run always use separate
databases: `EnsureCreated()` is a no-op on a database that already has tables,
so sharing one hides the newest tables and reports false failures (found on
the first local run, now guarded and regression-tested).

## Evidence from the first qualification run

Candidate: composed `#718` head `6f34095f4b625872889dfb693860d5a14615f3b9` +
`#720` head `fc880d8520c9781f137aa46e06e39becb34392d3`
= merge `798baa063e6cb5ef25326616327413e3665d699b`.
Servers: PostgreSQL 18.7 and PostgreSQL 15.20, throwaway clusters.

| Leg | Result |
| --- | --- |
| offline guard contracts | 28/28 pass |
| `schema-diff` ladder (82 migrations) PG15 -> PG18 | pass, 1931 columns identical |
| `billing` on PG18 | pass, 72/72 |
| `autopilot` on PG18 | pass, 224/224 |
| `billing-control` on PG15 | pass, 72/72 (matches PG18) |
| `autopilot-control` on PG15 | pass, 224/224 (matches PG18) |
| extension assumption audit | 82 migration classes scanned, no `CREATE EXTENSION` dependency, so stock `postgres:18` is a faithful parity target for the production `postgres-ssl:18` image |

Refused (red) cases, each verified to stop before opening a connection:

* `push` to `main` -> refused: ref outside the `ci/postgres18-` prefix.
* inherited Railway-shaped production URL -> refused: host not disposable.
* non-disposable database name (`taslim`) on loopback -> refused.
* PG15 server presented as the PG18 target -> refused: major mismatch.
* ladder database == test database -> refused.
* empty target connection string -> refused (no silent default).

## What this lane does not do

It does not enable a runtime, change any production configuration, perform
migrations against a real database, or lift the security / spending / billing /
privacy / rollback P0 HOLD. PG18 compatibility is necessary qualification and
nothing more: `#699` / `#690` / `#717`, the Bridge durable-fencing requirement,
and the incomplete global capacity inventory stay independent blockers, and the
`ci.yml` owner still has to decide how (or whether) the `autopilot-gate.if`
allowlist is extended for stacked bases.
