import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { formatMovieTimecode } from "@/lib/movieSalvage";

/**
 * Companion to `src/lib/movieSalvageLongFormTimecode.test.ts` (the #1104
 * test-only handoff) for the other long-form scale on the client: the Edit
 * module's ruler, total and playhead, which print whole-film seconds through
 * `formatEditTime`. For a legal 120-minute plan the ruler used to run its
 * minutes field past 59 and read "120:00"; it now rolls into hours while the
 * sub-hour scene/clip readout stays byte-identical.
 *
 * Synthetic integers only: no fetch, no provider client, no cost/usage
 * reservation, no DB, no real customer data.
 */

const workspaceSource = readFileSync(new URL("./FullMovieWorkspaceView.tsx", import.meta.url), "utf8");
const selectsSource = readFileSync(new URL("./MovieSelectsWorkspace.tsx", import.meta.url), "utf8");

/** Mirrors the component's wrapper: seconds -> integer ms, no ms field. */
function editTime(seconds: number): string {
  return formatMovieTimecode(Math.max(0, Math.floor(seconds * 1000)), { showMilliseconds: false });
}

describe("movie edit ruler long-form timecode (#685 client lane)", () => {
  it("keeps the sub-hour ruler and scene readout unchanged", () => {
    // One provider-capped clip or scene never leaves this scale; its current
    // MM:SS shape is the contract to preserve.
    expect(editTime(0)).toBe("00:00");
    expect(editTime(10)).toBe("00:10");
    expect(editTime(10.4)).toBe("00:10");
    expect(editTime(75)).toBe("01:15");
    expect(editTime(3_599)).toBe("59:59");
  });

  it("renders the 60- and 120-minute ruler ends with an hours field", () => {
    // model.durationSeconds for a two-hour plan is 7,200; the ruler prints
    // 0, the midpoint and the total through formatEditTime.
    expect(editTime(3_600)).toBe("01:00:00");
    expect(editTime(3_600 / 2)).toBe("30:00");
    expect(editTime(7_200)).toBe("02:00:00");
    expect(editTime(7_200 / 2)).toBe("01:00:00");
    expect(editTime(4_530.5)).toBe("01:15:30");
  });

  it("never lets the ruler minutes field run past 59 across the plan scale", () => {
    for (let minutes = 1; minutes <= 120; minutes += 1) {
      const rendered = editTime(minutes * 60);
      const fields = rendered.split(":");
      if (minutes >= 60) {
        expect(fields).toHaveLength(3);
      }
      expect(Number(fields.length === 3 ? fields[1] : fields[0])).toBeLessThan(60);
    }
  });

  it("routes the ruler, total and playhead through the shared helper", () => {
    // The wrapper must stay the single long-form-aware formatter so the
    // minutes field can never regress past 59 on this surface.
    expect(workspaceSource).toMatch(
      /function formatEditTime\(seconds: number\) \{\s*return formatMovieTimecode\(Math\.max\(0, Math\.floor\(seconds \* 1000\)\), \{ showMilliseconds: false \}\);\s*\}/,
    );
    expect(workspaceSource).toContain('import { formatMovieTimecode } from "@/lib/movieSalvage";');
    expect(workspaceSource).toContain("<span>{formatEditTime(model.durationSeconds)} total</span>");
    expect(workspaceSource).toContain("{formatEditTime(model.durationSeconds / 2)}");
    expect(workspaceSource).toContain("{formatEditTime(model.durationSeconds)}");
  });

  it("keeps the Selects whole-film total on the shared salvage formatter", () => {
    expect(selectsSource).toContain("formatSalvageTime(timeline.currentRevision.durationMilliseconds)} total");
  });
});
