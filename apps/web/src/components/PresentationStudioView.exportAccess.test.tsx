import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { locales, translate, translations } from "@/lib/i18n";
import { presentationExportAccessibleName, presentationExportCaption } from "@/lib/presentationExportAccess";

/**
 * Wiring contract for the Presentation result's download/export menu.
 *
 * Synthetic fixtures only: this reads the component source and asserts how it binds
 * the pure `presentationExportAccess` decisions into the export toolbar. No provider,
 * network, API client or user deck is involved. The naming and focus decisions
 * themselves are verified in `presentationExportAccess.test.ts`, and focus is
 * verified through the decision module rather than a browser, matching how
 * `ChatView.thread-focus.test.tsx` and the route recovery boundaries do it.
 */
const viewSource = readFileSync(new URL("./PresentationStudioView.tsx", import.meta.url), "utf8");

/** Return the export toolbar, from its container up to the "Open assets" link. */
function exportToolbarSource(): string {
  const start = viewSource.indexOf('<div className="presentation-output-actions">');
  expect(start, "PresentationStudioView must render a presentation-output-actions toolbar").toBeGreaterThan(-1);
  const end = viewSource.indexOf('<Link className="secondary-button" href="/assets">', start);
  expect(end, "the export toolbar must keep its Open assets link").toBeGreaterThan(start);
  return viewSource.slice(start, end);
}

/** Return the body of the download handler that owns the busy claim. */
function downloadHandlerSource(): string {
  const start = viewSource.indexOf("async function downloadRepresentation");
  expect(start, "PresentationStudioView must keep a downloadRepresentation handler").toBeGreaterThan(-1);
  const end = viewSource.indexOf("function createAnother", start);
  expect(end, "the download handler must close before createAnother").toBeGreaterThan(start);
  return viewSource.slice(start, end);
}

describe("Presentation export toolbar focus wiring", () => {
  it("never applies the disabled attribute to an export control", () => {
    const toolbar = exportToolbarSource();
    // The bug: `disabled={working}` hit the button the user had just activated, so the
    // browser blurred it, focus fell back to document.body for the whole blob download,
    // and it never returned to the menu.
    expect(toolbar).not.toMatch(/(?<!aria-)\bdisabled=\{/);
    expect(toolbar).not.toContain("disabled={working}");
  });

  it("reports the in-flight state on the control instead of removing it", () => {
    const toolbar = exportToolbarSource();
    expect(toolbar).toContain("aria-busy={exportControl.busy || undefined}");
    expect(toolbar).toContain("aria-disabled={exportControl.blocked || undefined}");
    expect(toolbar).toContain(
      "const exportControl = resolvePresentationExportControl({ working, pendingExportId, exportId: representation.id });",
    );
  });

  it("still refuses a second export while one is downloading", () => {
    // Without the attribute the guard is the only thing preserving one-download-per-view.
    expect(exportToolbarSource()).toContain(
      "onClick={() => { if (canActivatePresentationExport(exportControl)) void downloadRepresentation(representation.id, representation.fileName); }}",
    );
  });

  it("keeps the busy claim alive for exactly as long as the download", () => {
    const handler = downloadHandlerSource();
    expect(handler).toMatch(/setWorking\(true\);\s*setPendingExportId\(representationId\);/);
    expect(handler).toMatch(/setWorking\(false\);\s*setPendingExportId\(null\);/);
    // A claim that outlived `working` would pin a button busy with no download running.
    expect(handler.match(/setPendingExportId\(/g)).toHaveLength(2);
  });

  it("declares the pending claim beside the busy flag it belongs to", () => {
    const stateStart = viewSource.indexOf("const [working, setWorking] = useState(false);");
    expect(stateStart).toBeGreaterThan(-1);
    expect(viewSource.slice(stateStart, stateStart + 260)).toContain(
      'const [pendingExportId, setPendingExportId] = useState<string | null>(null);',
    );
  });
});

describe("Presentation export toolbar label wiring", () => {
  it("names each export control through the shared decision", () => {
    expect(exportToolbarSource()).toContain(
      'aria-label={presentationExportAccessibleName({ action: t("presentation.downloadLabel"), caption: presentationExportCaption(representation.type), fileName: representation.fileName })}',
    );
  });

  it("keeps the visible caption inside the accessible name", () => {
    // WCAG 2.5.3: a voice user says the word they can see.
    const toolbar = exportToolbarSource();
    const captionExpression = "<Download size={15} /> {presentationExportCaption(representation.type)}</button>";
    expect(toolbar).toContain(captionExpression);
    // Both halves come from the same call, so the name cannot drift from the caption.
    expect(toolbar.match(/presentationExportCaption\(representation\.type\)/g)).toHaveLength(2);
    const name = presentationExportAccessibleName({
      action: translate("en", "presentation.downloadLabel"),
      caption: presentationExportCaption("pptx"),
      fileName: "quarterly-sales.pptx",
    });
    expect(name).toContain(presentationExportCaption("pptx"));
    expect(name).toBe("Download PPTX: quarterly-sales.pptx");
  });

  it("renders one button per downloadable rendition through the shared selector", () => {
    const toolbar = exportToolbarSource();
    expect(toolbar).toContain("selectPresentationExports(result.representations)");
    // The inline filter duplicated the definition of "downloadable" inside the markup.
    expect(toolbar).not.toContain('representation.type === "pptx"');
  });

  it("ships the download action label in every supported locale", () => {
    expect(translations.en["presentation.downloadLabel"]).toBe("Download");
    for (const locale of locales) {
      const value = translate(locale, "presentation.downloadLabel");
      expect(value, `${locale} must translate the export action`).not.toBe("presentation.downloadLabel");
      expect(value.trim()).not.toBe("");
    }
    // Arabic and Sorani keep the Latin format token, so the name still contains the caption.
    for (const locale of locales) {
      expect(
        presentationExportAccessibleName({
          action: translate(locale, "presentation.downloadLabel"),
          caption: presentationExportCaption("pptx"),
          fileName: "deck.pptx",
        }),
      ).toContain("PPTX");
    }
  });

  it("leaves the rest of the result toolbar alone", () => {
    // Slide navigation, speaker notes and deck metadata are separate surfaces.
    const toolbar = exportToolbarSource();
    expect(toolbar).not.toContain("slide-nav");
    expect(toolbar).not.toContain("notes");
    expect(toolbar).not.toContain("theme");
  });
});
