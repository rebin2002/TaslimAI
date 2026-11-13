import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import {
  MovieMakeoverComparisonPanel,
  MOVIE_MAKEOVER_COMPARISON_DEFAULT_LABELS,
  clampMakeoverSplitPercent,
  isAllowedMakeoverPreviewUrl,
  movieMakeoverPaneState,
  movieMakeoverSplitPercentForKey,
  type MovieMakeoverComparisonLabels,
  type MovieMakeoverComparisonPanelProps,
  type MovieMakeoverComparisonSource,
} from "./MovieMakeoverComparisonPanel";

const panelSource = readFileSync(
  new URL("./MovieMakeoverComparisonPanel.tsx", import.meta.url),
  "utf8",
);

const beforeBlob = "blob:https://studio.taslim.test/00000000-0000-4000-8000-0000000000b4";
const afterBlob = "blob:https://studio.taslim.test/00000000-0000-4000-8000-0000000000b5";

const beforeSource: MovieMakeoverComparisonSource = {
  url: beforeBlob,
  durationSeconds: 42,
  width: 1920,
  height: 1080,
};
const afterSource: MovieMakeoverComparisonSource = {
  url: afterBlob,
  durationSeconds: 42,
  width: 1920,
  height: 1080,
};

// A controlled component must never fire its callbacks during a static render.
const onSplitPercentChange: MovieMakeoverComparisonPanelProps["onSplitPercentChange"] = () => {
  throw new Error("onSplitPercentChange must not fire during static render");
};
const onViewModeChange: MovieMakeoverComparisonPanelProps["onViewModeChange"] = () => {
  throw new Error("onViewModeChange must not fire during static render");
};

function render(
  overrides: Partial<MovieMakeoverComparisonPanelProps> = {},
): string {
  const props: MovieMakeoverComparisonPanelProps = {
    beforeSource,
    afterSource,
    viewMode: "split",
    splitPercent: 62,
    onSplitPercentChange,
    onViewModeChange,
    ...overrides,
  };
  return renderToStaticMarkup(<MovieMakeoverComparisonPanel {...props} />);
}

describe("MovieMakeoverComparisonPanel url admission (fail-closed, provider-free)", () => {
  it("accepts only authorized local/blob/http preview schemes", () => {
    expect(isAllowedMakeoverPreviewUrl(beforeBlob)).toBe(true);
    expect(isAllowedMakeoverPreviewUrl("data:video/mp4;base64,AAAA")).toBe(true);
    expect(isAllowedMakeoverPreviewUrl("https://cdn.taslim.test/preview.mp4")).toBe(true);
    expect(isAllowedMakeoverPreviewUrl("http://localhost:3000/preview.mp4")).toBe(true);
    expect(isAllowedMakeoverPreviewUrl("/previews/asset-77.mp4")).toBe(true);
  });
  it("rejects scripting, file and unverifiable sources", () => {
    expect(isAllowedMakeoverPreviewUrl("javascript:alert(1)")).toBe(false);
    expect(isAllowedMakeoverPreviewUrl("file:///home/user/secret.mp4")).toBe(false);
    expect(isAllowedMakeoverPreviewUrl("//cdn.taslim.test/preview.mp4")).toBe(false);
    expect(isAllowedMakeoverPreviewUrl("previews/asset-77.mp4")).toBe(false);
    expect(isAllowedMakeoverPreviewUrl("   ")).toBe(false);
    expect(movieMakeoverPaneState({ url: "javascript:alert(1)" })).toEqual({ kind: "blocked" });
    expect(movieMakeoverPaneState(null)).toEqual({ kind: "unavailable" });
    expect(movieMakeoverPaneState({ url: beforeBlob })).toEqual({ kind: "ready", url: beforeBlob });
  });
});

describe("MovieMakeoverComparisonPanel split percent semantics", () => {
  it("clamps non-finite and out-of-range values", () => {
    expect(clampMakeoverSplitPercent(-40)).toBe(0);
    expect(clampMakeoverSplitPercent(140)).toBe(100);
    expect(clampMakeoverSplitPercent(Number.NaN)).toBe(50);
    expect(clampMakeoverSplitPercent(62.6)).toBe(63);
  });
  it("moves the reveal by keyboard steps and mirrors horizontal arrows in rtl", () => {
    expect(movieMakeoverSplitPercentForKey("ArrowRight", 62, "ltr")).toBe(63);
    expect(movieMakeoverSplitPercentForKey("ArrowLeft", 62, "ltr")).toBe(61);
    expect(movieMakeoverSplitPercentForKey("ArrowRight", 62, "rtl")).toBe(61);
    expect(movieMakeoverSplitPercentForKey("ArrowLeft", 62, "rtl")).toBe(63);
    expect(movieMakeoverSplitPercentForKey("ArrowUp", 62, "ltr")).toBe(72);
    expect(movieMakeoverSplitPercentForKey("ArrowDown", 62, "rtl")).toBe(52);
    expect(movieMakeoverSplitPercentForKey("PageUp", 90, "ltr")).toBe(100);
    expect(movieMakeoverSplitPercentForKey("PageDown", 10, "ltr")).toBe(0);
    expect(movieMakeoverSplitPercentForKey("Home", 62, "ltr")).toBe(0);
    expect(movieMakeoverSplitPercentForKey("End", 62, "rtl")).toBe(100);
    expect(movieMakeoverSplitPercentForKey("Enter", 62, "ltr")).toBeNull();
  });
});

describe("MovieMakeoverComparisonPanel controlled static render", () => {
  it("renders both authorized panes and slider semantics without firing callbacks", () => {
    const html = render();
    expect(html).toContain('aria-label="AI Video Makeover before and after comparison"');
    expect(html).toContain(`src="${beforeBlob}"`);
    expect(html).toContain(`src="${afterBlob}"`);
    expect(html).toContain('role="slider"');
    expect(html).toContain('tabindex="0"');
    expect(html).toContain('aria-valuenow="62"');
    expect(html).toContain('aria-valuetext="62%"');
    // Logical position mirrors under dir without any shared CSS.
    expect(html).toContain("inset-inline-start:62%");
    expect(html).toContain("clip-path:inset(0 38% 0 0)");
    expect(html).toContain('aria-pressed="true"');
    expect(html).toContain('aria-pressed="false"');
  });
  it("keeps a missing makeover output an honest unavailable state, never a fake result", () => {
    const html = render({ afterSource: null });
    expect(html).toContain('role="status"');
    expect(html).toContain(MOVIE_MAKEOVER_COMPARISON_DEFAULT_LABELS.afterUnavailable);
    // The before video must not be presented as if it were the makeover result.
    expect(html).not.toContain(`src="${afterBlob}"`);
    expect(html.split(beforeBlob).length - 1).toBe(1);
    expect(html).not.toContain('aria-label="Makeover result"');
  });
  it("blocks a disallowed scheme before it can reach any media element", () => {
    const html = render({
      beforeSource: { url: "javascript:alert(document.cookie)" },
      afterSource: { url: afterBlob, posterUrl: "javascript:alert(2)" },
    });
    expect(html).not.toContain("src=\"javascript:");
    expect(html).not.toContain("poster=\"javascript:");
    expect(html).toContain('role="status"');
    expect(html).toContain(MOVIE_MAKEOVER_COMPARISON_DEFAULT_LABELS.blocked);
  });
  it("shows exactly one pane per before/after mode and hides the splitter", () => {
    const onlyBefore = render({ viewMode: "before" });
    expect(onlyBefore).toContain(`src="${beforeBlob}"`);
    expect(onlyBefore).not.toContain(`src="${afterBlob}"`);
    expect(onlyBefore).not.toContain('role="slider"');
    const onlyAfter = render({ viewMode: "after" });
    expect(onlyAfter).toContain(`src="${afterBlob}"`);
    expect(onlyAfter).not.toContain(`src="${beforeBlob}"`);
    expect(onlyAfter).not.toContain('role="slider"');
  });
  it("mirrors layout and accepts rtl-localized labels", () => {
    const soraniLabels: Partial<MovieMakeoverComparisonLabels> = {
      region: "بەراوردی پێش و دواتری میۆکئۆڤەری ڤیدیۆ",
      splitter: "جیاتی نیشاندانی ڕەسەن و ئەنجام",
      afterPane: "ئەنجامی میۆکئۆڤەر",
    };
    const html = render({ dir: "rtl", labels: soraniLabels, splitPercent: 62 });
    expect(html).toContain('dir="rtl"');
    expect(html).toContain('aria-label="بەراوردی پێش و دواتری میۆکئۆڤەری ڤیدیۆ"');
    expect(html).toContain('aria-label="جیاتی نیشاندانی ڕەسەن و ئەنجام"');
    expect(html).toContain('aria-label="ئەنجامی میۆکئۆڤەر"');
    // The before slice is kept on the right (inline-start) edge under rtl.
    expect(html).toContain("clip-path:inset(0 0 0 38%)");
    expect(html).toContain("inset-inline-start:62%");
  });
});

describe("MovieMakeoverComparisonPanel ownership and network boundaries", () => {
  const importLine = /^import\s.+from\s+["'](.+)["'];$/gm;
  const importedModules = [...panelSource.matchAll(importLine)].map((match) => match[1]);

  it("imports nothing from the open #698 sibling slices", () => {
    for (const modulePath of importedModules) {
      expect(modulePath).not.toMatch(/movieVideoMakeoverDraftState/);
      expect(modulePath).not.toMatch(/movieVideoMakeoverInputState/);
      expect(modulePath).not.toMatch(/MovieVideoMakeoverInputPanel/);
      expect(modulePath).not.toMatch(/FullMovieWorkspaceView/);
    }
    // Only React type imports may exist at all in this pure presentational slice.
    expect(importedModules).toEqual(["react"]);
  });
  it("performs no network requests and claims no shared stylesheet or i18n keys", () => {
    expect(panelSource).not.toContain("fetch(");
    expect(panelSource).not.toContain("XMLHttpRequest");
    expect(panelSource).not.toContain("WebSocket");
    expect(panelSource).not.toMatch(/import\s+[^;]*\.css/);
    expect(panelSource).not.toContain("@/lib/i18n");
    expect(panelSource).not.toContain("createElement(\"script\"");
  });
});
