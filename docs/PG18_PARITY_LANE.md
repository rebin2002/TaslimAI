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

## Hosted execution (GitHub Actions, free runners, disposable databases)

The lane executes from its own `ci/postgres18-` branch namespace, so it does not
wait on a `.github/workflows/ci.yml` owner merge. `workflow_dispatch` is still
accepted, but GitHub only lists a dispatchable workflow once the file exists on
the default branch, which is why the prefix trigger is the primary path.

Safe-default proof, run 43349372716 on `a51898b346c40e80b8d7ea34f2e5075231e6db76`
and re-proved on the docs-only push run for `c306aa13b77907b704582450ff0a71c2a5631579`:

| Job | Result |
| --- | --- |
| Resolve opt-in legs | success -> `legs=guards-only` |
| Parity harness guard contracts | success (36/36) |
| Autopilot / Billing / schema-parity legs | **skipped**, as designed without a marker |

Full lane on `main` + this lane, run 43350243764 on `cd5806621091270391a75c7f44548f7e8a1dfefe`,
and full lane on the **composed #718 + #720 candidate**, run 43350275586 on
`726aabc24ece737e8232491d6dfa7443642d1669` (merge of `#718` head `6f34095f`,
`#720` head `fc880d85`, and this lane):

| Leg | Composed-candidate result | Job |
| --- | --- | --- |
| Guard contracts | 36/36 pass | [job](https://github.com/rebin2002/TaslimAI/actions/runs/43350275586/job/131639415163) |
| Autopilot on PG18 | pass, 224/224, `target server major = 18` | [job](https://github.com/rebin2002/TaslimAI/actions/runs/43350275586/job/131639466601) |
| Billing on PG18 | pass, 72/72, `target server major = 18` | [job](https://github.com/rebin2002/TaslimAI/actions/runs/43350275586/job/131639466590) |
| PG15 control vs PG18 schema | pass, `PG15 control server major = 15`, 1931 columns identical | [job](https://github.com/rebin2002/TaslimAI/actions/runs/43350275586/job/131639466611) |
| Extension assumption audit | 82 migration classes, no `CREATE EXTENSION` dependency | (all legs) |

`726aabc2` is a disposable evidence branch under the lane prefix, not a PR and
not a release candidate; it can be deleted once reviewed. The hosted PG18 Autopilot
leg executes the same bounded suite that #720's exact-head run left **skipped**,
which is the specific qualification gap #734 records.

## Red-to-green history of this slice

1. First local Autopilot leg reported 4 failures. Root cause was the harness, not
   PostgreSQL: the ladder and the test run shared one database, so
   `EnsureCreated()` became a no-op and `AutopilotLocks` / `AutopilotRuns` were
   missing (`42P01`). Fixed by separating the databases and adding a refusal when
   they collide.
2. The harness then passed 224/224 on PG18 while still reporting `EXIT=1`, because
   the summary check string-matched `Failed: 0` while `dotnet test` pads the
   counter (`Failed:     0`). Fixed by parsing the counters and asserting
   `failed == 0 and passed > 0`.
3. First hosted billing leg failed with `Run "dotnet tool restore" to make the
   "dotnet-ef" command available` - the job was missing the step the other jobs
   had. Fixed, plus a harness preflight that now fails with an actionable message.
4. Final hosted state: all legs green on `main` + lane and on the composed
   #718 + #720 candidate.

## What this lane does not do

It does not enable a runtime, change any production configuration, perform
migrations against a real database, or lift the security / spending / billing /
privacy / rollback P0 HOLD. PG18 compatibility is necessary qualification and
nothing more: `#699` / `#690` / `#717`, the Bridge durable-fencing requirement,
and the incomplete global capacity inventory stay independent blockers, and the
`ci.yml` owner still has to decide how (or whether) the `autopilot-gate.if`
allowlist is extended for stacked bases.
