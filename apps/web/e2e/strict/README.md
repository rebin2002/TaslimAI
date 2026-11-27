# Strict post-#613 production `next start` acceptance gate (form-encoded malformed Next-Action)

Test-only, direct literal IPv4 loopback, synthetic requests. Requested by the 2026-11-18 release-gate QA
review on PR #1063 (`COMMENT`, head `aab09c35`): the #1063 harness intentionally
accepts `expect([400, 404, 500])` as a backward-compatible **containment** contract,
which means all suites can be green while the original production **500** (issue #697,
form-encoded `Next-Action: x`) is still reproducible. This lane is the separate
**strict acceptance** gate; it replaces nothing.

## What it asserts (STRICT)

For each of the four primary #1063 shapes — form-urlencoded `POST /` with
`next-action: "x"` (prod literal) and an unrecognized 40-hex id, each with `Origin`
sent and omitted — the response must be **non-5xx**, plus `GET /` baseline and
recovery 200s. Any 500 (the unpatched defect) is a hard FAIL.

## Anti-vacuity guarantee

Every gate invocation (except `--self-test`, which checks both directions) also runs
the same evaluator against an embedded deterministic **negative-control** loopback
server that answers the exact malformed shape with a fixed 500. The evaluator must
FAIL that control or the gate refuses to certify the real server (exit 2). A green
here is therefore proof the gate has teeth, not that it always passes.

## Usage (opt-in only — never auto-discovered, not wired into CI)

```bash
cd apps/web
node e2e/strict/next-start-strict-gate.mjs --self-test   # evaluator discrimination, no build needed
node e2e/strict/next-start-strict-gate.mjs --url http://127.0.0.1:PORT   # gate an isolated, externally started prod server
```

Exit codes: `0` PASS, `1` FAIL (5xx observed), `2` harness error / vacuous-gate guard.
Only literal `http://127.0.0.1:PORT` with an explicit valid port is accepted. The harness uses direct HTTP without DNS, ambient proxy configuration or redirect following. The former `--serve` mode is disabled to prevent inherited credentials and proxy settings. Start an isolated synthetic production image separately using the known-good hosted container procedure, with no credentials or production services, and restrict its network as appropriate. The harness cannot guarantee that the separately started application makes no outbound calls. Never target production.

## Historical and current evidence (not release approval)

The older build of `main@52a39468` returned four 500s. A later isolated hosted image [run 45859988653](https://github.com/rebin2002/TaslimAI/actions/runs/45859988653) on base `65e8598cfee1f3ceb5cdf908aaba5050a35b6f43`, harness `257f4884b54a2ca13c8a595049a4a6d673e9c8fb`, returned four 404s, baseline/recovery 200 and correctly rejected the deterministic 500 control. This applies to the earlier harness only. Repeat self-test, exact-head CI and isolated real-server acceptance for this hardened revision. Preserve independent Security and #1063 route acceptance. This remains opt-in and replaces no existing check.

## Ownership / disjointness

Only adds files under `apps/web/e2e/strict/`. Does not touch
`e2e/server-action-form-encoded-boundary.regression.spec.ts` (#1063),
`e2e/server-action-malformed-post.spec.ts` (merged #764), `playwright.config.ts`,
`src/app/not-found.tsx`, `src/app/not-found.test.tsx`, `src/components/LocaleProvider.tsx`
(#613), `.github/workflows/ci.yml`, or any source file. Playwright's default
`testMatch` (`**/*.spec.ts`) and `vitest` configs never collect `*.mjs` here, and CI
never invokes this directory.
