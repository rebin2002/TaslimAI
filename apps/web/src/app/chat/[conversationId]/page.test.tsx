import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("conversation route loading boundary", () => {
  const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

  it("provides a semantic loading status and hides the decorative spinner", () => {
    expect(source).toContain('role="status"');
    expect(source).toContain('aria-live="polite"');
    expect(source).toContain('aria-busy="true"');
    expect(source).toContain('aria-hidden="true"');
  });

  it("keeps the server-side authentication gate from the merged auth integration", () => {
    // #824 made this route server-gated. The loading-boundary change must not
    // regress that protection.
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    const authCallIndex = source.indexOf("await requireAuthenticatedPage");
    const suspenseIndex = source.indexOf("<Suspense");
    expect(authCallIndex).toBeGreaterThanOrEqual(0);
    expect(authCallIndex).toBeLessThan(suspenseIndex);
  });
});
