import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./DevelopmentBuildChecklist.tsx", import.meta.url), "utf8");
const styles = readFileSync(new URL("./DevelopmentBuildChecklist.module.css", import.meta.url), "utf8");

describe("development build checklist accessibility structure", () => {
  it("keeps the checklist local and exposes labelled progress and controls", () => {
    expect(source).toContain("readDevelopmentBuildState");
    expect(source).toContain("saveDevelopmentBuildState");
    expect(source).toContain('role="progressbar"');
    expect(source).toContain("aria-describedby=\"development-build-note-hint\"");
    expect(source).not.toContain("api.");
  });

  it("includes keyboard focus and reduced-motion affordances", () => {
    expect(styles).toContain(":focus-visible");
    expect(styles).toContain("prefers-reduced-motion: reduce");
    expect(styles).toContain('[dir="rtl"] .backLink');
  });
});
