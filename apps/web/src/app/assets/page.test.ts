import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Assets route authorization", () => {
  it("checks the session on the server before rendering the private asset library", () => {
    expect(source).toContain('import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage(internalReturnPath("/assets", filters));');
    expect(source).toContain("return <ProtectedPage><AssetsView");
  });

  it("gates on the server before the private library renders", () => {
    const gate = source.indexOf("await requireAuthenticatedPage(");
    const render = source.indexOf("return <ProtectedPage>");
    expect(gate).toBeGreaterThan(-1);
    expect(gate).toBeLessThan(render);
  });

  it("reads the decoded query before the gate so deep-link filters survive the redirect", () => {
    // /assets hydrates assetId/projectId/search/status from the query string,
    // so `next` has to be the whole location rather than the bare pathname.
    const awaitFilters = source.indexOf("const filters = await searchParams;");
    const gate = source.indexOf('await requireAuthenticatedPage(internalReturnPath("/assets", filters));');
    expect(awaitFilters).toBeGreaterThan(-1);
    expect(awaitFilters).toBeLessThan(gate);
  });
});
