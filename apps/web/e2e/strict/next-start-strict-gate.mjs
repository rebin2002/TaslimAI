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
 *   --url <base>           Run the gate against an externally started
 *                          production server (loopback only).
 *
 * Exit codes: 0 PASS, 1 FAIL (5xx observed / recovery broken),
 * 2 harness error or vacuous-gate guard tripped.
 *
 * Test-only, direct literal loopback. The externally started app must be isolated
 * from credentials and external services. No production probes.
 */
import { Agent, createServer, request } from "node:http";

const FETCH_TIMEOUT_MS = 15_000;

const FORM_ACTION_IDS = [
  ["literal \"x\" (prod shape)", "x"],
  ["unrecognized 40-hex id", "0000000000000000000000000000000000000000"],
];
const FORM_BODY = "email=anon%40example.test&note=synthetic";

function parseLoopbackPort(url) {
  const match = /^http:\/\/127\.0\.0\.1:([1-9]\d{0,4})$/.exec(url);
  if (!match || match[0] !== url || Number(match[1]) > 65535) {
    throw new Error("Only literal http://127.0.0.1:PORT (1-65535) is allowed.");
  }
  return Number(match[1]);
}

async function httpFetch(baseUrl, init = {}) {
  const port = parseLoopbackPort(baseUrl);
  // Explicit direct agent; no DNS, ambient proxy configuration or redirects.
  const agent = new Agent({ keepAlive: false });
  return new Promise((resolve, reject) => {
    const headers = { ...init.headers };
    if (init.body !== undefined) headers["content-length"] = Buffer.byteLength(init.body);
    const req = request({
      hostname: "127.0.0.1", port, path: "/", method: init.method ?? "GET",
      headers, agent,
    }, (res) => {
      res.on("error", reject);
      res.on("aborted", () => reject(new Error("Loopback response aborted")));
      res.on("end", () => resolve({ status: res.statusCode }));
      res.resume();
    });
    const timer = setTimeout(() => req.destroy(new Error("Loopback request timed out")), FETCH_TIMEOUT_MS);
    req.on("error", reject);
    req.on("close", () => { clearTimeout(timer); agent.destroy(); });
    req.end(init.body);
  });
}

/**
 * The strict evaluator. Pure decision function over a base URL so the exact
 * same code path certifies the real `next start` server, the synthetic
 * post-fix control and the deterministic 500 negative control.
 */
async function evaluateStrictGate(baseUrl) {
  const cases = [];
  let pass = true;

  const baseline = await httpFetch(baseUrl);
  cases.push({ name: "baseline GET / is 200", ok: baseline.status === 200, status: baseline.status });
  if (baseline.status !== 200) pass = false;

  for (const [label, actionId] of FORM_ACTION_IDS) {
    for (const withOrigin of [true, false]) {
      const headers = {
        "next-action": actionId,
        "content-type": "application/x-www-form-urlencoded",
      };
      if (withOrigin) headers.origin = new URL(baseUrl).origin;
      const res = await httpFetch(baseUrl, { method: "POST", body: FORM_BODY, headers });
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

  const recovery = await httpFetch(baseUrl);
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

function parseArgs(argv) {
  if (argv.length === 1 && argv[0] === "--self-test") return { mode: "self-test" };
  if (argv.length === 2 && argv[0] === "--url") {
    parseLoopbackPort(argv[1]);
    return { mode: "url", url: argv[1] };
  }
  throw new Error("Usage: --self-test | --url http://127.0.0.1:PORT; --serve is disabled.");
}

function verifyTargetGuard() {
  const denied = [
    "http://localhost:3000", "http://example.invalid:3000",
    "http://127.0.0.1", "http://127.0.0.1:0", "http://127.0.0.1:65536",
    "http://127.1:3000", "http://2130706433:3000", "http://[::1]:3000",
    "https://127.0.0.1:3000", "http://127.0.0.1:3000/path",
    "http://127.0.0.1:3000@outside.invalid", "http://127.0.0.1:3000?x=1",
    "http://127.0.0.1:3000\n",
  ];
  for (const url of denied) {
    let rejected = false;
    try { parseLoopbackPort(url); } catch { rejected = true; }
    if (!rejected) throw new Error("Target guard accepted a disallowed URL");
  }
  if (parseLoopbackPort("http://127.0.0.1:3000") !== 3000) {
    throw new Error("Target guard rejected literal loopback");
  }
  console.log("[self-test] literal loopback target guard: OK");
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.mode === "self-test") {
    verifyTargetGuard();
    const positive = await runPositiveControl();
    printResult("control/fixed-shape(400) — must PASS", positive);
    const negative = await runNegativeControl();
    printResult("negative-control/broken(500) — must FAIL", negative);
    const good = positive.verdict === "PASS" && negative.verdict === "FAIL";
    console.log(`\n[self-test] evaluator discrimination: ${good ? "OK" : "BROKEN"}`);
    process.exit(good ? 0 : 1);
  }

  const targetUrl = args.url;
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
}

void main().catch((error) => {
  console.error("[strict-gate] harness error:", error.message);
  process.exitCode = 2;
});
