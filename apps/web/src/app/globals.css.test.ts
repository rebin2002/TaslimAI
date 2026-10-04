import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const imageStudioStyles = readFileSync(new URL("./globals.css", import.meta.url), "utf8");

describe("Image Studio accessibility styles", () => {
  it("keeps every interactive control keyboard-visible", () => {
    expect(imageStudioStyles).toContain(
      '.image-studio-page :is(a, button, input, select, textarea, summary):focus-visible { outline: 2px solid #8de0d1; outline-offset: 3px; }',
    );
  });

  it("honors reduced-motion preferences for Image Studio animations and transitions", () => {
    expect(imageStudioStyles).toContain("@media (prefers-reduced-motion: reduce)");
    expect(imageStudioStyles).toContain("animation-duration: 0.01ms !important");
    expect(imageStudioStyles).toContain("transition-duration: 0.01ms !important");
    expect(imageStudioStyles).toContain("scroll-behavior: auto !important");
  });
});
