import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { AccessibleProgressBar, clampAccessibleProgress } from "./AccessibleProgressBar";

const imageSource = readFileSync(new URL("./ImageStudioView.tsx", import.meta.url), "utf8");
const musicSource = readFileSync(new URL("./MusicStudioView.tsx", import.meta.url), "utf8");
const researchSource = readFileSync(new URL("./ResearchStudioView.tsx", import.meta.url), "utf8");

describe("AccessibleProgressBar", () => {
  it("clamps values and exposes progress semantics to assistive technology", () => {
    const html = renderToStaticMarkup(<AccessibleProgressBar className="test-progress" label="Generation progress" value={140} />);

    expect(clampAccessibleProgress(-10)).toBe(0);
    expect(clampAccessibleProgress(140)).toBe(100);
    expect(clampAccessibleProgress(undefined)).toBe(0);
    expect(html).toContain('role="progressbar"');
    expect(html).toContain('aria-label="Generation progress"');
    expect(html).toContain('aria-valuemin="0"');
    expect(html).toContain('aria-valuemax="100"');
    expect(html).toContain('aria-valuenow="100"');
    expect(html).toContain('aria-valuetext="100%"');
    expect(html).toContain('style="width:100%"');
  });

  it("keeps Image, Music, and Research Studio progress indicators on the shared accessible implementation", () => {
    expect(imageSource).toContain('import { AccessibleProgressBar } from "@/components/AccessibleProgressBar";');
    expect(imageSource).toContain('<AccessibleProgressBar className="image-progress-track"');
    expect(musicSource).toContain('import { AccessibleProgressBar } from "@/components/AccessibleProgressBar";');
    expect(musicSource).toContain('<AccessibleProgressBar className="music-progress-track"');
    expect(researchSource).toContain('import { AccessibleProgressBar } from "@/components/AccessibleProgressBar";');
    expect(researchSource).toContain('<AccessibleProgressBar className="generation-progress-track"');
  });
});
