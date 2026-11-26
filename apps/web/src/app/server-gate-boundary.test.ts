import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

const appRoot = join(new URL(".", import.meta.url).pathname);

function isFile(path: string): boolean {
  try {
    return statSync(path).isFile();
  } catch {
    return false;
  }
}

const routeDirs: string[] = [""];
(function walk(dir: string) {
  for (const entry of readdirSync(join(appRoot, dir), { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const next = dir ? `${dir}/${entry.name}` : entry.name;
    routeDirs.push(next);
    walk(next);
  }
})("");

const gatedPages = routeDirs
  .map((dir) => (dir ? `${dir}/page.tsx` : "page.tsx"))
  .filter((file) => isFile(join(appRoot, file)) && readFileSync(join(appRoot, file), "utf8").includes("requireAuthenticatedPage"))
  .sort();

function layoutsAbove(pageFile: string): string[] {
  const segments = pageFile.split("/").slice(0, -1); // drop page.tsx
  const found: string[] = [];
  for (let depth = 0; depth <= segments.length; depth += 1) {
    const prefix = segments.slice(0, depth);
    const candidate = prefix.length ? `${prefix.join("/")}/layout.tsx` : "layout.tsx";
    if (isFile(join(appRoot, candidate))) found.push(candidate);
  }
  return found;
}

const clientLayoutsAboveGates = gatedPages
  .map((pageFile) => ({
    pageFile,
    offenders: layoutsAbove(pageFile).filter(
      (layout) => readFileSync(join(appRoot, layout), "utf8").startsWith('"use client";'),
    ),
  }))
  .filter((entry) => entry.offenders.length > 0);

describe("server-side page gates and their layout boundaries", () => {
  it("walks the route tree it is meant to cover", () => {
    // Fails loudly instead of going vacuously green if the walk breaks.
    expect(gatedPages.length).toBeGreaterThanOrEqual(30);
    expect(gatedPages).toEqual(expect.arrayContaining([
      "assets/page.tsx",
      "account/memory/page.tsx",
      "account/generation-jobs/page.tsx",
      join("create", "presentation", "page.tsx"),
    ]));
  });

  it("has no client-component layout above any server gate", () => {
    // A "use client" layout receives the page as a `children` prop. A
    // redirect() thrown by a gated page underneath that boundary is reported to
    // the nearest error boundary (src/app/error.tsx) instead of becoming a
    // navigation, so the gate answers HTTP 200 with an error page rather than
    // the 307 to /login it promises — the gate is silently inert. This is the
    // shape that made the batch-2 /account/memory gate ineffective until the
    // boundary moved below the gate.
    expect(clientLayoutsAboveGates).toEqual([]);
  });
});
