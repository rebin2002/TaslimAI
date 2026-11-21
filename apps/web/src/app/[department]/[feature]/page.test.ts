import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const routeSource = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("dynamic department feature route authorization", () => {
  it("requires server authentication before rendering a valid feature placeholder", () => {
    expect(routeSource).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(routeSource).toContain('export const dynamic = "force-dynamic";');
    expect(routeSource).toContain("await requireAuthenticatedPage(`/${department}/${feature}`);");

    const authCallIndex = routeSource.indexOf("await requireAuthenticatedPage");
    const routeLookupIndex = routeSource.indexOf("const entry = departments.find");
    expect(authCallIndex).toBeGreaterThanOrEqual(0);
    expect(authCallIndex).toBeLessThan(routeLookupIndex);
  });
});
