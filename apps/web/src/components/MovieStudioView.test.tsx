import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { locales, translate } from "@/lib/i18n";
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

  it("routes the quick-result status chip through translation keys instead of hardcoded English", () => {
    const chipLine = source.split("\n").find((line) => line.includes("const label = outputAssetId"));
    expect(chipLine, "quick-result status chip line").toBeDefined();
    // Regression: the chip previously rendered raw "Ready to review"/"Brief saved",
    // which bypassed key lookup so no locale or installable zh-CN pack could cover it.
    expect(chipLine).not.toContain('"Ready to review"');
    expect(chipLine).not.toContain('"Brief saved"');
    expect(chipLine).toContain('t("movie.completed")');
    expect(chipLine).toContain('t("movie.briefSaved")');
  });

  it("keeps every status chip value translated in every launch locale", () => {
    for (const locale of locales) {
      expect(translate(locale, "movie.briefSaved"), `${locale}: movie.briefSaved`).not.toBe("movie.briefSaved");
      expect(translate(locale, "movie.briefSaved").trim(), `${locale}: movie.briefSaved`).not.toBe("");
      expect(translate(locale, "movie.completed"), `${locale}: movie.completed`).not.toBe("movie.completed");
      for (const status of ["preparing", "queued", "generating", "completed", "failed"]) {
        expect(translate(locale, `movie.state.${status}`), `${locale}: movie.state.${status}`).not.toBe(`movie.state.${status}`);
      }
    }
    expect(translate("en", "movie.briefSaved")).toBe("Brief saved");
    expect(translate("en", "movie.completed")).toBe("Ready to review");
  });
});
