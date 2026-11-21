import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const pageSource = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Chat route loading fallback", () => {
  it("announces the Suspense loading state without exposing the decorative spinner", () => {
    expect(pageSource).toContain('role="status"');
    expect(pageSource).toContain('aria-live="polite"');
    expect(pageSource).toContain('aria-busy="true"');
    expect(pageSource).toContain('aria-hidden="true"');
  });
});
