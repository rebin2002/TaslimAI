import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { locales, translate } from "@/lib/i18n";
import { selectPresentationExports } from "@/lib/presentationExportAccess";
import {
  filterStudioDownloadableRepresentations,
  readStudioRepresentationDownloadability,
} from "@/lib/studioRepresentationAvailability";

/**
 * Wiring contract for the Presentation export menu's live availability gate
 * (#1049, consumer half of the #1117 contract; sibling style to
 * `PresentationStudioView.exportAccess.test.tsx`).
 *
 * Synthetic fixtures and component-source assertions only: no provider,
 * network, API client or real deck. The focus/naming decisions themselves stay
 * owned by `presentationExportAccess.ts` and its test - this lane only adds
 * the per-rendition availability fact and may not disturb them.
 */
const viewSource = readFileSync(new URL("./PresentationStudioView.tsx", import.meta.url), "utf8");

function exportToolbarSource(): string {
  const start = viewSource.indexOf('<div className="presentation-output-actions">');
  expect(start, "PresentationStudioView must render a presentation-output-actions toolbar").toBeGreaterThan(-1);
  const end = viewSource.indexOf('<Link className="secondary-button" href="/assets">', start);
  expect(end, "the export toolbar must keep its Open assets link").toBeGreaterThan(start);
  return viewSource.slice(start, end);
}

const PPTX_READY = "00000000-0000-4000-8000-000000000021";
const PPTX_PRUNED = "00000000-0000-4000-8000-000000000022";

describe("Presentation export toolbar availability wiring (#1049)", () => {
  it("reads the live facts from the job DTO beside the snapshot", () => {
    expect(viewSource).toContain(
      "const representationDownloadability = readStudioRepresentationDownloadability(current);",
    );
  });
  it("gates the export menu after the type selector, keeping both decisions composed", () => {
    const toolbar = exportToolbarSource();
    expect(toolbar).toContain(
      "filterStudioDownloadableRepresentations(selectPresentationExports(result.representations), representationDownloadability)",
    );
    // The pre-#1117 selector expression itself must remain inside the wrapper.
    expect(toolbar).toContain("selectPresentationExports(result.representations)");
  });
  it("does not introduce the disabled attribute that the focus doctrine forbids here", () => {
    const toolbar = exportToolbarSource();
    expect(toolbar).not.toMatch(/(?<!aria-)\bdisabled=\{/);
  });
});

describe("Presentation export menu availability behaviour", () => {
  const renditions = [
    { id: PPTX_READY, type: "pptx", fileName: "quarterly-deck.pptx" },
    { id: PPTX_PRUNED, type: "pptx", fileName: "quarterly-deck-pruned.pptx" },
    { id: "00000000-0000-4000-8000-000000000023", type: "pdf", fileName: "quarterly-deck.pdf" },
  ];

  it("withholds only the pruned rendition: a dead PPTX leaves its ready sibling advertised", () => {
    const facts = readStudioRepresentationDownloadability({
      representationDownloads: [
        { id: PPTX_READY, type: "pptx", canDownload: true },
        { id: PPTX_PRUNED, type: "pptx", canDownload: false },
      ],
    });
    const visible = filterStudioDownloadableRepresentations(selectPresentationExports(renditions), facts);
    expect(visible.map((item) => item.id)).toEqual([PPTX_READY]);
  });

  it("is a byte-for-byte no-op on legacy jobs without the companion field", () => {
    const facts = readStudioRepresentationDownloadability({ id: "job-1", resultJson: "{}" });
    expect(facts).toBeNull();
    expect(filterStudioDownloadableRepresentations(selectPresentationExports(renditions), facts)).toEqual(renditions.filter((r) => r.type === "pptx"));
  });

  it.each([...locales])("the gate depends on ids only, identical outcome under %s labels", (locale) => {
    // The locale can never change which rendition is advertised: availability is
    // keyed by representation id, and the label lookup stays intact for the
    // retained control. Arabic and Sorani keep the Latin format token (PR #975).
    const facts = readStudioRepresentationDownloadability({
      representationDownloads: [
        { id: PPTX_READY, canDownload: true },
        { id: PPTX_PRUNED, canDownload: false },
      ],
    });
    const visible = filterStudioDownloadableRepresentations(selectPresentationExports(renditions), facts);
    expect(visible.map((item) => item.id)).toEqual([PPTX_READY]);
    const label = translate(locale, "presentation.downloadLabel");
    expect(label.trim()).not.toBe("");
    expect(label).not.toBe("presentation.downloadLabel");
  });
});
