import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const styles = readFileSync(new URL("./globals.css", import.meta.url), "utf8");

describe("shared app shell accessibility styles", () => {
  it("keeps the bypass link hidden until keyboard focus and above the sticky shell", () => {
    expect(styles).toContain(".skip-link {");
    expect(styles).toContain(".skip-link:focus-visible {");
    expect(styles).toContain("inset-inline-start: 12px;");
    expect(styles).toContain("z-index: 100;");
    expect(styles).toContain("transform: translateY(0);");
  });

  it("keeps the focused main landmark clear of the sticky header", () => {
    expect(styles).toContain("#main-content { scroll-margin-top: 96px; }");
  });

  it("preserves reduced-motion behavior for the focus transition", () => {
    expect(styles).toContain("@media (prefers-reduced-motion: reduce)");
    expect(styles).toContain(".skip-link { transition: none; }");
  });
});
