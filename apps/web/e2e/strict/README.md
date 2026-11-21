# Strict post-#613 production `next start` acceptance gate (form-encoded malformed Next-Action)

Test-only, loopback-only, provider-free. Requested by the 2026-11-18 release-gate QA
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
NEXT_PUBLIC_API_URL=https://synthetic.invalid npx next build
node e2e/strict/next-start-strict-gate.mjs --serve       # spawns real `next start`, gates it, tears down
node e2e/strict/next-start-strict-gate.mjs --url http://127.0.0.1:PORT   # gate an externally started prod server
```

Exit codes: `0` PASS, `1` FAIL (5xx observed), `2` harness error / vacuous-gate guard.
Non-loopback `--url` values are rejected outright (no production probes).

## Status on current `main` (unpatched)

`--serve` on `main@52a39468` build `IBk1sNQbLNqdtLcfVgNZN`: **FAIL exit 1** — all four
form-encoded cases return real 500s. This is the gate working as designed; the
original 500 is **not** claimed fixed. Enable this gate as a release blocker only
after the #613 (or successor) source fix lands, at which point the expectation is a
`0` exit.

## Ownership / disjointness

Only adds files under `apps/web/e2e/strict/`. Does not touch
`e2e/server-action-form-encoded-boundary.regression.spec.ts` (#1063),
`e2e/server-action-malformed-post.spec.ts` (merged #764), `playwright.config.ts`,
`src/app/not-found.tsx`, `src/app/not-found.test.tsx`, `src/components/LocaleProvider.tsx`
(#613), `.github/workflows/ci.yml`, or any source file. Playwright's default
`testMatch` (`**/*.spec.ts`) and `vitest` configs never collect `*.mjs` here, and CI
never invokes this directory.
