import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { isQuickMovieProviderReady, movieDraftDurationLimit, nextMovieMode } from "./MovieStudioView";

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

describe("Movie Studio Quick Movie provider readiness", () => {
  it("lets users save Quick and Full Movie briefs without a video-generation provider", () => {
    // The create-project API saves metadata and returns job:null in both modes;
    // it never submits a paid generation job. Readiness gates belong to later
    // video-generation operations, not the safe brief-save flow.
    expect(source).toContain("await api.createMovieProject({");
    expect(source).toContain("setSaved(result.project)");
    expect(source).toContain("setJob(result.job)");
    expect(source).not.toContain("canCreateMovieMode(");
    expect(source).toContain("disabled={working || form.description.trim().length < 3}");
    expect(source).toContain('t("movieBody.quick.quickNote")');
  });

  it("advertises Quick Movie only when the configured provider is ready for quick_movie", () => {
    expect(isQuickMovieProviderReady(null)).toBe(false);
    expect(isQuickMovieProviderReady({ ready: false, supportedOperations: ["quick_movie"] })).toBe(false);
    expect(isQuickMovieProviderReady({ ready: true, supportedOperations: [] })).toBe(false);
    expect(isQuickMovieProviderReady({ ready: true, supportedOperations: ["scene_clip"] })).toBe(false);
    expect(isQuickMovieProviderReady({ ready: true, supportedOperations: ["scene_clip", "quick_movie"] })).toBe(true);
    expect(isQuickMovieProviderReady({ ready: true, supportedOperations: ["QUICK_MOVIE"] })).toBe(true);
  });
});

// Keep 120-minute film *plans* distinct from any single provider clip.
// Mode switching must not apply the Full timeline limit to Quick briefs.
describe("Movie Studio project duration controls", () => {
  it("allows two-hour Full Movie planning without expanding Quick brief limits", () => {
    expect(movieDraftDurationLimit("Quick")).toBe(3_600);
    expect(movieDraftDurationLimit("Full")).toBe(7_200);
    expect(source).toContain("max={movieDraftDurationLimit(mode)}");
  });

  it("preserves each mode\u0027s chosen duration for the draft-only create request", () => {
    expect(source).toContain("useState<Record<MovieMode, number>>({ Quick: 30, Full: 30 })");
    expect(source).toContain("value={durationByMode[mode]}");
    expect(source).toContain("[mode]: Number(event.target.value)");
    expect(source).toContain("durationSeconds: durationByMode[mode],");
  });
});
