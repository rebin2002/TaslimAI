import { describe, expect, it } from "vitest";
import {
  canActivatePresentationExport,
  presentationExportAccessibleName,
  presentationExportCaption,
  resolvePresentationExportControl,
  selectPresentationExports,
} from "./presentationExportAccess";

/**
 * Synthetic fixtures only: literal rendition records shaped like
 * `PresentationJobResult["representations"]`. No provider, no network, no API
 * client, no translation catalog and no real deck.
 */
type Representation = { id: string; type: string; fileName: string; contentType: string };

const deckFile: Representation = {
  id: "pptx-1",
  type: "pptx",
  fileName: "quarterly-sales.pptx",
  contentType: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
};
/** The second PowerPoint rendition is the shape `parsePresentationJobResult` accepts. */
const deckCopy: Representation = {
  id: "pptx-2",
  type: "pptx",
  fileName: "quarterly-sales-copy.pptx",
  contentType: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
};
const pdfFile: Representation = { id: "pdf-1", type: "pdf", fileName: "quarterly-sales.pdf", contentType: "application/pdf" };

/** Strings the component resolves from the catalog; the module itself is locale-free. */
const action = "Download";

function nameOf(representation: Representation) {
  return presentationExportAccessibleName({
    action,
    caption: presentationExportCaption(representation.type),
    fileName: representation.fileName,
  });
}

describe("Presentation export selection", () => {
  it("offers exactly the renditions the menu claims to offer, in result order", () => {
    expect(selectPresentationExports([deckFile, pdfFile, deckCopy]).map((r) => r.id)).toEqual(["pptx-1", "pptx-2"]);
  });

  it("treats a result without representations as having nothing to export", () => {
    expect(selectPresentationExports([])).toEqual([]);
    expect(selectPresentationExports(undefined)).toEqual([]);
    expect(selectPresentationExports(null)).toEqual([]);
    expect(selectPresentationExports([pdfFile])).toEqual([]);
  });

  it("matches the normalized type, so a stored uppercase extension is still offered", () => {
    expect(selectPresentationExports([{ ...deckCopy, type: "PPTX" }]).map((r) => r.id)).toEqual([]);
    // `parseRepresentations` lower-cases into the type; the menu must not re-normalize
    // it, which is what the caption is for.
    expect(selectPresentationExports([{ ...deckCopy, type: "pptx" }]).map((r) => r.id)).toEqual(["pptx-2"]);
  });
});

describe("Presentation export captions and accessible names", () => {
  it("keeps the caption the toolbar has always shown", () => {
    expect(presentationExportCaption("pptx")).toBe("PPTX");
  });

  it("names the action instead of only the format", () => {
    // The defect: the accessible name was `PPTX`, which is a file extension, not a
    // purpose. Voice-control users had no word to say, and a screen reader gave no
    // indication that activating the button downloads anything.
    expect(nameOf(deckFile)).toBe("Download PPTX: quarterly-sales.pptx");
    expect(nameOf(deckFile).toLowerCase()).toContain("download");
  });

  it("keeps the visible caption inside the accessible name (label in name)", () => {
    const caption = presentationExportCaption(deckFile.type);
    expect(nameOf(deckFile)).toContain(caption);
  });

  it("distinguishes two renditions of the same format", () => {
    // Both buttons used to read "PPTX" and nothing else.
    expect(nameOf(deckFile)).not.toBe(nameOf(deckCopy));
    expect([nameOf(deckFile), nameOf(deckCopy)]).toEqual([
      "Download PPTX: quarterly-sales.pptx",
      "Download PPTX: quarterly-sales-copy.pptx",
    ]);
  });

  it("still names the action when a rendition carries no usable file name", () => {
    expect(nameOf({ ...deckFile, fileName: "   " })).toBe("Download PPTX");
    expect(nameOf({ ...deckFile, fileName: "" })).toBe("Download PPTX");
  });

  it("reports the same name while its download is in flight as while it is idle", () => {
    // A control that is renamed while it holds focus is announced as if a new
    // control had appeared, so progress must travel on aria-busy, never on the name.
    const inFlight = resolvePresentationExportControl({ working: true, pendingExportId: deckFile.id, exportId: deckFile.id });
    // The in-flight state carries no name, so the toolbar has nothing left to
    // rewrite on the control that holds focus.
    expect(inFlight).not.toHaveProperty("name");
    expect(inFlight).not.toHaveProperty("label");
    // And the name is a pure function of the rendition, which is what keeps it stable.
    expect(nameOf(deckFile)).toBe(presentationExportAccessibleName({ action, caption: "PPTX", fileName: deckFile.fileName }));
  });
});

describe("Presentation export focus and activation state", () => {
  it("marks only the activated control busy", () => {
    expect(resolvePresentationExportControl({ working: true, pendingExportId: deckFile.id, exportId: deckFile.id })).toEqual({ busy: true, blocked: false });
    expect(resolvePresentationExportControl({ working: true, pendingExportId: deckFile.id, exportId: deckCopy.id })).toEqual({ busy: false, blocked: true });
  });

  it("marks nothing busy or blocked once the download settles", () => {
    expect(resolvePresentationExportControl({ working: false, pendingExportId: deckFile.id, exportId: deckFile.id })).toEqual({ busy: false, blocked: false });
    expect(resolvePresentationExportControl({ working: false, pendingExportId: null, exportId: deckCopy.id })).toEqual({ busy: false, blocked: false });
  });

  it("leaves a pending claim from an earlier export from reporting busy once idle", () => {
    // `working` is the lifetime of the claim, so a stale id cannot pin a button busy.
    const state = resolvePresentationExportControl({ working: false, pendingExportId: "pptx-9", exportId: "pptx-9" });
    expect(state.busy).toBe(false);
    expect(canActivatePresentationExport(state)).toBe(true);
  });

  it("refuses a second export while one is in flight, which is what disabled was for", () => {
    const activated = resolvePresentationExportControl({ working: true, pendingExportId: deckFile.id, exportId: deckFile.id });
    const sibling = resolvePresentationExportControl({ working: true, pendingExportId: deckFile.id, exportId: deckCopy.id });
    expect(canActivatePresentationExport(activated)).toBe(false);
    expect(canActivatePresentationExport(sibling)).toBe(false);
    expect(canActivatePresentationExport(resolvePresentationExportControl({ working: false, pendingExportId: null, exportId: deckFile.id }))).toBe(true);
  });

  it("never expresses the in-flight state as an unavailable element", () => {
    // Guard against reintroducing the focus loss in another form: the contract is two
    // booleans that map onto aria-busy / aria-disabled, never a hidden or removed node.
    const running = resolvePresentationExportControl({ working: true, pendingExportId: deckFile.id, exportId: deckCopy.id });
    expect(Object.keys(running).sort()).toEqual(["blocked", "busy"]);
    expect(running).not.toHaveProperty("disabled");
    expect(running).not.toHaveProperty("hidden");
  });
});
