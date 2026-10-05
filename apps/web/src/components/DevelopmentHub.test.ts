import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./DevelopmentHub.tsx", import.meta.url), "utf8");
const styles = readFileSync(new URL("./DevelopmentHub.module.css", import.meta.url), "utf8");

describe("development hub accessibility structure", () => {
  it("provides a labelled navigation landmark and current-route semantics", () => {
    expect(source).not.toContain("<main");
    expect(source).toContain('<section className={styles.page} aria-labelledby="development-hub-title">');
    expect(source).toContain('<nav className={styles.navigation} aria-label={strings.navigationLabel}>');
    expect(source).toContain('aria-current={active ? "page" : undefined}');
    expect(source).toContain("aria-hidden=\"true\"");
  });

  it("keeps keyboard focus and reduced-motion affordances in the module stylesheet", () => {
    expect(styles).toContain(":focus-visible");
    expect(styles).toContain("prefers-reduced-motion: reduce");
    expect(styles).toContain('[dir="rtl"] .backLink svg');
  });
});
