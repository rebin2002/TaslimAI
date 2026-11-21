#!/usr/bin/env node
/**
 * STRICT, opt-in production `next start` smoke acceptance gate for the
 * form-encoded malformed Next-Action POST regression (issue #697), per the
 * 2026-11-18 release-gate QA review on PR #1063.
 *
 * Contract (STRICT, unlike the merged/pending containment contracts):
 *   - POST / with `content-type: application/x-www-form-urlencoded` and a
 *     malformed `next-action` id (prod literal "x" + an unrecognized 40-hex
 *     id, each with Origin sent and omitted — the four primary #1063 cases)
 *     must return a NON-5xx status (400/404 post-fix, or a framework
 *     200/3xx). Any 5xx is a FAIL — including 500, which the backward-
 *     compatible containment spec in
 *     `e2e/server-action-form-encoded-boundary.regression.spec.ts` (#1063)
 *     still accepts. This gate REPLACES nothing; it is the post-#613
 *     acceptance lane only and must not be treated as green evidence until
 *     the #613 source fix is integrated.
 *   - Recovery: GET / immediately afterwards must be 200.
 *
 * Deterministic negative control (embedded in EVERY gate invocation):
 *   A loopback-only synthetic server that answers the exact malformed
 *   form-encoded shape with a deterministic HTTP 500 is run through the
 *   same evaluator. The evaluator MUST report FAIL against it. If it does
 *   not, this gate refuses to certify the real server (exit code 2), so a
 *   PASS can never be a vacuous green.
 *
 * Modes:
 *   --self-test            Synthetic fixed-shape (400) and broken-shape
 *                          (500) servers; asserts the evaluator decides
 *                          both correctly. No app build required.
 *   --serve                Spawn `next start` against the existing
 *                          production build in apps/web/.next, run the
 *                          strict gate + embedded negative control, tear
 *                          the server down. Requires `next build` first.
 *   --url <base>           Run the gate against an externally started
 *                          production server (loopback only).
 *
 * Exit codes: 0 PASS, 1 FAIL (5xx observed / recovery broken),
 * 2 harness error or vacuous-gate guard tripped.
 *
 * Test-only, provider-free, loopback-only. No secrets, no production
 * probes, no source changes. Uses Node >= 18 built-ins only.
 */
import { createServer } from "node:http";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import net from "node:net";
import path from "node:path";

const WEB_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const FETCH_TIMEOUT_MS = 15_000;
const READY_TIMEOUT_MS = 60_000;

const FORM_ACTION_IDS = [
  ["literal \"x\" (prod shape)", "x"],
  ["unrecognized 40-hex id", "0000000000000000000000000000000000000000"],
];
const FORM_BODY = "email=anon%40example.test&note=synthetic";

async function httpFetch(url, init = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
  try {
    const res = await fetch(url, { redirect: "manual", signal: controller.signal, ...init });
    const body = await res.text().catch(() => "");
    return { status: res.status, headers: res.headers, body };
  } finally {
    clearTimeout(timer);
  }
}

/**
 * The strict evaluator. Pure decision function over a base URL so the exact
 * same code path certifies the real `next start` server, the synthetic
 * post-fix control and the deterministic 500 negative control.
 */
async function evaluateStrictGate(baseUrl) {
  const cases = [];
  let pass = true;

  const baseline = await httpFetch(`${baseUrl}/`);
  cases.push({ name: "baseline GET / is 200", ok: baseline.status === 200, status: baseline.status });
  if (baseline.status !== 200) pass = false;

  for (const [label, actionId] of FORM_ACTION_IDS) {
    for (const withOrigin of [true, false]) {
      const headers = {
        "next-action": actionId,
        "content-type": "application/x-www-form-urlencoded",
      };
      if (withOrigin) headers.origin = new URL(baseUrl).origin;
      const res = await httpFetch(`${baseUrl}/`, { method: "POST", body: FORM_BODY, headers });
      // STRICT: any 5xx fails. 400/404 (post-fix) and framework 200/3xx pass.
      const ok = res.status < 500;
      cases.push({
        name: `POST / form-encoded malformed next-action (${label}, origin ${withOrigin ? "sent" : "omitted"}) is non-5xx`,
        ok,
        status: res.status,
      });
      if (!ok) pass = false;
    }
  }

  const recovery = await httpFetch(`${baseUrl}/`);
  cases.push({ name: "recovery GET / is 200 after malformed storm", ok: recovery.status === 200, status: recovery.status });
  if (recovery.status !== 200) pass = false;

  return { verdict: pass ? "PASS" : "FAIL", cases };
}

function makeSyntheticServer({ mode }) {
  // mode "fixed": post-#613-shaped framework rejection (400).
  // mode "broken": deterministic negative control — answers the exact
  // malformed form-encoded shape with a stable 500 (unpatched prod defect).
  return createServer((req, res) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => {
      void chunks;
      if (req.method === "GET") {
        res.writeHead(200, { "content-type": "text/html" });
        res.end("<html><body>synthetic-ok</body></html>");
        return;
      }
      if (req.method === "POST" && req.url === "/") {
        if (mode === "broken") {
          res.writeHead(500, { "content-type": "text/html" });
          res.end("<html><body>synthetic-deterministic-500</body></html>");
          return;
        }
        res.writeHead(400, { "content-type": "text/plain" });
        res.end("Invalid Server Action request.");
        return;
      }
      res.writeHead(404, { "content-type": "text/plain" });
      res.end("not found");
    });
  });
}

async function listen(server) {
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  return `http://127.0.0.1:${port}`;
}

async function freePort() {
  const srv = net.createServer();
  await new Promise((resolve) => srv.listen(0, "127.0.0.1", resolve));
  const { port } = srv.address();
  await new Promise((resolve) => srv.close(resolve));
  return port;
}

/**
 * Negative control: the deterministic-500 server MUST make the strict
 * evaluator FAIL. Returns true when the gate's teeth are confirmed.
 */
async function runNegativeControl() {
  const broken = makeSyntheticServer({ mode: "broken" });
  const url = await listen(broken);
  try {
    const result = await evaluateStrictGate(url);
    return result;
  } finally {
    await new Promise((resolve) => broken.close(resolve));
  }
}

async function runPositiveControl() {
  const fixed = makeSyntheticServer({ mode: "fixed" });
  const url = await listen(fixed);
  try {
    return await evaluateStrictGate(url);
  } finally {
    await new Promise((resolve) => fixed.close(resolve));
  }
}

function printResult(title, result) {
  console.log(`\n[${title}] verdict=${result.verdict}`);
  for (const c of result.cases) {
    console.log(`  ${c.ok ? "ok  " : "FAIL"}  ${c.name}  (status=${c.status})`);
  }
}

async function spawnNextStart(port) {
  const buildIdPath = path.join(WEB_DIR, ".next", "BUILD_ID");
  if (!existsSync(buildIdPath)) {
    console.error(`No production build found at ${buildIdPath}.`);
    console.error("Run first (loopback, synthetic env only): cd apps/web && NEXT_PUBLIC_API_URL=https://synthetic.invalid npx next build");
    process.exit(2);
  }
  console.log(`[serve] using build ${readFileSync(buildIdPath, "utf8").trim()} via next start on 127.0.0.1:${port}`);
  // `next` may be hoisted to the workspace root (npm workspaces) — resolve
  // it the same way the local `next` bin shim does, with a direct fallback.
  const require_ = createRequire(path.join(WEB_DIR, "package.json"));
  let nextBin;
  try {
    nextBin = require_.resolve("next/dist/bin/next");
  } catch {
    nextBin = path.join(WEB_DIR, "node_modules", "next", "dist", "bin", "next");
  }
  const child = spawn(process.execPath, [nextBin, "start", "-p", String(port), "-H", "127.0.0.1"], {
    cwd: WEB_DIR,
    stdio: ["ignore", "pipe", "pipe"],
    env: { ...process.env, NODE_ENV: "production", NEXT_TELEMETRY_DISABLED: "1" },
  });
  let log = "";
  child.stdout.on("data", (d) => { log += d; });
  child.stderr.on("data", (d) => { log += d; });
  const deadline = Date.now() + READY_TIMEOUT_MS;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) {
      console.error("[serve] next start exited early:\n" + log);
      process.exit(2);
    }
    try {
      const probe = await httpFetch(`http://127.0.0.1:${port}/`);
      if (probe.status === 200) return child;
    } catch {
      // not listening yet
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  child.kill("SIGKILL");
  console.error("[serve] next start never became ready:\n" + log);
  process.exit(2);
}

function parseArgs(argv) {
  const args = { mode: "", url: "" };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === "--self-test") args.mode = "self-test";
    else if (argv[i] === "--serve") args.mode = "serve";
    else if (argv[i] === "--url") { args.mode = "url"; args.url = argv[i + 1] ?? ""; i += 1; }
    else { console.error(`Unknown argument: ${argv[i]}`); process.exit(2); }
  }
  return args;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (!args.mode) {
    console.log("Usage: node next-start-strict-gate.mjs --self-test | --serve | --url <loopback-base-url>");
    process.exit(2);
  }

  if (args.mode === "self-test") {
    const positive = await runPositiveControl();
    printResult("control/fixed-shape(400) — must PASS", positive);
    const negative = await runNegativeControl();
    printResult("negative-control/broken(500) — must FAIL", negative);
    const good = positive.verdict === "PASS" && negative.verdict === "FAIL";
    console.log(`\n[self-test] evaluator discrimination: ${good ? "OK" : "BROKEN"}`);
    process.exit(good ? 0 : 1);
  }

  let targetUrl = args.url;
  let child = null;
  if (args.mode === "serve") {
    targetUrl = `http://127.0.0.1:${await freePort()}`;
    child = await spawnNextStart(new URL(targetUrl).port);
  }
  if (!/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?$/.test(targetUrl)) {
    console.error("Refusing: --url must be loopback-only (http://127.0.0.1:PORT or http://localhost:PORT). No production probes.");
    process.exit(2);
  }

  try {
    const result = await evaluateStrictGate(targetUrl);
    printResult(`strict-gate/${targetUrl}`, result);

    // Embedded negative control: a green on the real server is only
    // meaningful if the SAME evaluator demonstrably fails a deterministic
    // 500. If that control ever reports PASS, this gate cannot certify.
    const control = await runNegativeControl();
    printResult("negative-control/broken(500) — must FAIL", control);
    if (control.verdict !== "FAIL") {
      console.error("\n[gate-guard] negative control did NOT fail the evaluator; refusing to certify. Exit 2.");
      process.exit(2);
    }

    console.log(`\n[strict-gate] FINAL: ${result.verdict} (negative-control guard confirmed: a 500 fails this gate)`);
    process.exit(result.verdict === "PASS" ? 0 : 1);
  } finally {
    if (child && child.exitCode === null) child.kill("SIGKILL");
  }
}

void main();
