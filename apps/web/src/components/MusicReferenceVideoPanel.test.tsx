import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { ReactElement } from "react";
import {
  emptyReferenceVideoState,
  type ReferenceVideoCandidate,
  type ReferenceVideoState,
} from "@/lib/musicReferenceVideoState";
import { ReferenceVideoPanel } from "./MusicReferenceVideoPanel";

function candidate(overrides: Partial<ReferenceVideoCandidate> = {}): ReferenceVideoCandidate {
  return {
    id: "local-video:5242880:clip.mp4",
    sourceKind: "local",
    displayName: "clip.mp4",
    extension: ".mp4",
    mimeType: "video/mp4",
    sizeBytes: 5 * 1024 * 1024,
    assetId: null,
    durationSeconds: null,
    ...overrides,
  };
}

// The locale catalog returns the key itself when a translation is missing,
// which is exactly what keeps these render assertions deterministic.
const t = (key: string) => key;

function render(state: ReferenceVideoState, locale = "en"): string {
  return renderToStaticMarkup(
    <ReferenceVideoPanel state={state} locale={locale} t={t} onSelectFile={vi.fn()} onClear={vi.fn()} /> as ReactElement,
  );
}

describe("reference video panel states", () => {
  it("renders an accessible empty state without any file markup", () => {
    const html = render(emptyReferenceVideoState);
    expect(html).toContain('class="music-reference-video-panel"');
    expect(html).toContain("music-reference-video-heading");
    expect(html).toContain('aria-labelledby="');
    expect(html).toContain("music.referenceVideo.title");
    expect(html).toContain("music.referenceVideo.empty");
    expect(html).not.toContain("music.referenceVideo.loading");
    expect(html).not.toContain("clip.mp4");
  });

  it("shows the loading state ahead of selection or rejection", () => {
    const html = render({ ...emptyReferenceVideoState, loading: true });
    expect(html).toContain("music.referenceVideo.loading");
    expect(html).not.toContain("music.referenceVideo.empty");
  });

  it("renders the rejection copy for a wrong container, not the raw reason key", () => {
    const html = render({ ...emptyReferenceVideoState, rejectedReasonKey: "music.referenceVideo.reason.extensionUnsupported" });
    expect(html).toContain('role="alert"');
    expect(html).toContain("Reference videos must be MP4, MOV, or M4V files.");
    expect(html).not.toContain("music.referenceVideo.reason.extensionUnsupported");
  });

  it("renders the oversize rejection boundary in English", () => {
    const html = render({ ...emptyReferenceVideoState, rejectedReasonKey: "music.referenceVideo.reason.oversized" });
    expect(html).toContain("25 MB");
    expect(html).not.toContain("music.referenceVideo.reason.oversized");
  });

  it("localizes rejection copy for RTL locales", () => {
    const ar = render({ ...emptyReferenceVideoState, rejectedReasonKey: "music.referenceVideo.reason.durationTooLong" }, "ar");
    const ku = render({ ...emptyReferenceVideoState, rejectedReasonKey: "music.referenceVideo.reason.durationTooLong" }, "ckb");
    expect(ar).toContain("10 دقائق");
    expect(ku).toContain("١٠ خولەک");
    expect(ar).not.toContain("music.referenceVideo.reason.durationTooLong");
  });

  it("renders guaranteed copy for unknown reasons so nothing renders blank", () => {
    const html = render({ ...emptyReferenceVideoState, rejectedReasonKey: "music.referenceVideo.reason.invented" });
    expect(html).toContain('role="alert"');
    expect(html).toContain("Reference videos must be MP4, MOV, or M4V files.");
    expect(html).not.toContain("music.referenceVideo.reason.invented");
  });

  it("confirms an accepted candidate with its size and a clear action", () => {
    const html = render({ candidate: candidate(), rejectedReasonKey: null, loading: false });
    expect(html).toContain("clip.mp4");
    expect(html).toContain("5.0 MB");
    expect(html).toContain('class="music-reference-video-clear"');
    expect(html).toContain("music.referenceVideo.clear");
    expect(html).not.toContain('role="alert"');
  });

  it("does not render stored asset size markup when metadata is absent", () => {
    const html = render({
      candidate: candidate({ sourceKind: "stored_asset", assetId: "asset-1", displayName: "reference.mp4", sizeBytes: null }),
      rejectedReasonKey: null,
      loading: false,
    });
    expect(html).toContain("reference.mp4");
    expect(html).not.toContain("MB");
  });
});
