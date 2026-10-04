import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { nextMovieMode } from "./MovieStudioView";

const source = readFileSync(new URL("./MovieStudioView.tsx", import.meta.url), "utf8");

describe("Movie Studio mode switch accessibility", () => {
  it("cycles modes with standard tab-list keys", () => {
    expect(nextMovieMode("Quick", "ArrowRight")).toBe("Full");
    expect(nextMovieMode("Full", "ArrowRight")).toBe("Quick");
    expect(nextMovieMode("Quick", "ArrowLeft")).toBe("Full");
    expect(nextMovieMode("Quick", "Home")).toBe("Quick");
    expect(nextMovieMode("Quick", "End")).toBe("Full");
    expect(nextMovieMode("Quick", "Enter")).toBeNull();
  });

  it("exposes the mode switch as a labelled, roving-focus tablist", () => {
    expect(source).toContain('role="tablist"');
    expect(source.match(/role="tab"/g)).toHaveLength(2);
    expect(source).toContain('aria-selected={mode === "Quick"}');
    expect(source).toContain('aria-selected={mode === "Full"}');
    expect(source).toContain('aria-controls="movie-brief-panel"');
    expect(source).toContain('tabIndex={mode === "Quick" ? 0 : -1}');
    expect(source).toContain('tabIndex={mode === "Full" ? 0 : -1}');
    expect(source).toContain('onKeyDown={(event) => handleModeKeyDown(event, "Quick")}');
    expect(source).toContain('onKeyDown={(event) => handleModeKeyDown(event, "Full")}');
    expect(source).toContain('id="movie-brief-panel" role="tabpanel"');
    expect(source).toContain('aria-labelledby={`movie-mode-tab-${mode.toLowerCase()}`}');
  });
});
