import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("conversation route loading boundary", () => {
  it("provides a semantic loading status and hides the decorative spinner", () => {
    const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

    expect(source).toContain('role="status"');
    expect(source).toContain('aria-live="polite"');
    expect(source).toContain('aria-busy="true"');
    expect(source).toContain('aria-hidden="true"');
  });
});
