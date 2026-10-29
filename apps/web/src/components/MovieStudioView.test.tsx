import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { canCreateMovieMode, isQuickMovieProviderReady, nextMovieMode } from "./MovieStudioView";

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
  it("keeps provider-free Full Movie planning usable but prevents unsupported Quick Movie jobs", () => {
    const sceneOnly = { ready: true, supportedOperations: ["scene_clip"] };
    const quickReady = { ready: true, supportedOperations: ["quick_movie"] };

    expect(canCreateMovieMode("Quick", null)).toBe(false);
    expect(canCreateMovieMode("Quick", sceneOnly)).toBe(false);
    expect(canCreateMovieMode("Quick", quickReady)).toBe(true);
    expect(canCreateMovieMode("Full", null)).toBe(true);
    expect(canCreateMovieMode("Full", sceneOnly)).toBe(true);

    // The submit handler must guard direct submissions as well as disabling
    // its button; changing tabs cannot bypass the operation-readiness gate.
    expect(source).toContain('if (!canCreateMovieMode(mode, provider))');
    expect(source).toContain('form.description.trim().length < 3 || !canCreateMovieMode(mode, provider)');
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
