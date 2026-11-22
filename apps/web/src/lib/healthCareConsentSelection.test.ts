/**
 * Issue #714 (Health Care) — deterministic synthetic tests for the opt-in
 * consent / private-document selection controller.
 *
 * Every fixture below is synthetic: opaque identifiers (`synth-doc-*`),
 * invented labels and no real documents, patients or medical content.
 * Nothing here calls an API, storage, upload or model path; the controller
 * itself is dependency-free, so the assertions are pure state transitions.
 *
 * Ownership note: this file is independent of #715 (route/placeholder/i18n),
 * #793 (create route + StudioChooser), #794 (consent-revocation UI contract
 * test) and #729 (server-side membership revocation). It tests the shipped
 * source controller that those lanes can later adopt.
 */

import { describe, expect, it, vi } from "vitest";
import {
  consentSelectionLeakViolations,
  createConsentSelectionStore,
  describeConsentSelectionUi,
  initialConsentSelectionState,
  isDocumentSelected,
  isPreviewAvailable,
  reduceConsentSelection,
  selectedDocumentIds,
  type ConsentSelectionState,
  type PrivateDocument,
} from "./healthCareConsentSelection";

const SYNTH_DOCS: PrivateDocument[] = [
  { id: "synth-doc-01", label: "Synthetic Panel A", kind: "lab-report" },
  { id: "synth-doc-02", label: "Synthetic Panel B", kind: "imaging" },
  { id: "synth-doc-03", label: "Synthetic Panel C", kind: "note" },
];

function grantedState(): ConsentSelectionState {
  return reduceConsentSelection(initialConsentSelectionState, { type: "grant", documents: SYNTH_DOCS });
}

describe("healthCareConsentSelection controller (synthetic, #714)", () => {
  it("starts inert: no consent, no documents, no selection, no preview", () => {
    const state = initialConsentSelectionState;
    expect(state.consent).toBe("ungranted");
    expect(state.documents).toHaveLength(0);
    expect(selectedDocumentIds(state)).toEqual([]);
    expect(describeConsentSelectionUi(state).rows).toHaveLength(0);
    expect(describeConsentSelectionUi(state).consentLabel).toBe("opt-in");
  });

  it("selects and previews only what the user explicitly opted into", () => {
    let state = reduceConsentSelection(initialConsentSelectionState, { type: "grant", documents: SYNTH_DOCS });
    expect(selectedDocumentIds(state)).toEqual([]); // grant != selection
    state = reduceConsentSelection(state, { type: "toggle", documentId: "synth-doc-02" });
    expect(isDocumentSelected(state, "synth-doc-02")).toBe(true);
    expect(selectedDocumentIds(state)).toEqual(["synth-doc-02"]);
    state = reduceConsentSelection(state, { type: "preview", documentId: "synth-doc-02" });
    expect(isPreviewAvailable(state, "synth-doc-02")).toBe(true);
    // An unselected document can never be previewed.
    const rejected = reduceConsentSelection(state, { type: "preview", documentId: "synth-doc-01" });
    expect(rejected.preview).toEqual({ status: "unavailable", reason: "not-selected" });
  });

  it("clears all selections and disables previews immediately on revocation", () => {
    let state = reduceConsentSelection(initialConsentSelectionState, { type: "grant", documents: SYNTH_DOCS });
    state = reduceConsentSelection(state, { type: "toggle", documentId: "synth-doc-01" });
    state = reduceConsentSelection(state, { type: "toggle", documentId: "synth-doc-03" });
    state = reduceConsentSelection(state, { type: "preview", documentId: "synth-doc-01" });
    expect(isPreviewAvailable(state, "synth-doc-01")).toBe(true);

    const revoked = reduceConsentSelection(state, { type: "revoke" });

    expect(revoked.consent).toBe("revoked");
    expect(selectedDocumentIds(revoked)).toEqual([]);
    expect(revoked.selection).toEqual({});
    expect(revoked.preview).toEqual({ status: "unavailable", reason: "consent-revoked" });
    expect(isPreviewAvailable(revoked, "synth-doc-01")).toBe(false);
    expect(isDocumentSelected(revoked, "synth-doc-01")).toBe(false);
    expect(revoked.revocationCount).toBe(state.revocationCount + 1);
    // Nothing observable in the UI layer survives revocation.
    const ui = describeConsentSelectionUi(revoked);
    expect(ui.rows).toHaveLength(0);
    expect(ui.consentLabel).toBe("withdrawn");
    expect(ui.canPreviewAny).toBe(false);
  });

  it("never implicitly restores selection when consent is re-granted", () => {
    let state = reduceConsentSelection(initialConsentSelectionState, { type: "grant", documents: SYNTH_DOCS });
    state = reduceConsentSelection(state, { type: "toggle", documentId: "synth-doc-02" });
    state = reduceConsentSelection(state, { type: "revoke" });
    state = reduceConsentSelection(state, { type: "grant", documents: SYNTH_DOCS });

    expect(state.consent).toBe("granted");
    expect(selectedDocumentIds(state)).toEqual([]);
    expect(isPreviewAvailable(state, "synth-doc-02")).toBe(false);
    // Preview after re-grant requires a brand-new explicit selection.
    expect(reduceConsentSelection(state, { type: "preview", documentId: "synth-doc-02" }).preview).toEqual({
      status: "unavailable",
      reason: "not-selected",
    });
  });

  it("is fail-closed across a navigate-away/resume proposal while revoked", () => {
    let state = reduceConsentSelection(initialConsentSelectionState, { type: "grant", documents: SYNTH_DOCS });
    state = reduceConsentSelection(state, { type: "toggle", documentId: "synth-doc-01" });
    state = reduceConsentSelection(state, { type: "revoke" });

    const resumed = reduceConsentSelection(state, {
      type: "resume",
      proposal: { previouslySelectedDocumentIds: ["synth-doc-01", "synth-doc-03"] },
    });

    expect(resumed.consent).toBe("revoked");
    expect(resumed.selection).toEqual({});
    expect(selectedDocumentIds(resumed)).toEqual([]);
    expect(resumed.droppedResumeSelections).toBe(2);
    expect(resumed.preview).toEqual({ status: "unavailable", reason: "consent-revoked" });
  });

  it("drops a resume proposal even when it arrives before any consent", () => {
    const state = reduceConsentSelection(initialConsentSelectionState, {
      type: "resume",
      proposal: { previouslySelectedDocumentIds: ["synth-doc-01"] },
    });
    expect(state.consent).toBe("ungranted");
    expect(state.selection).toEqual({});
    expect(
      state.preview.status === "unavailable" && state.preview.reason === "no-consent"
        ? true
        : state.preview.status === "none",
    ).toBe(true);
  });

  it("keeps a valid carry-over selection intact while consent remains granted", () => {
    let state = reduceConsentSelection(initialConsentSelectionState, { type: "grant", documents: SYNTH_DOCS });
    state = reduceConsentSelection(state, { type: "toggle", documentId: "synth-doc-01" });
    const resumed = reduceConsentSelection(state, {
      type: "resume",
      proposal: { previouslySelectedDocumentIds: ["synth-doc-01"] },
    });
    expect(resumed).toBe(state); // no-op, still exactly one explicit selection
    expect(selectedDocumentIds(resumed)).toEqual(["synth-doc-01"]);
  });

  it("clears the active preview when its own selection is toggled off", () => {
    let state = reduceConsentSelection(initialConsentSelectionState, { type: "grant", documents: SYNTH_DOCS });
    state = reduceConsentSelection(state, { type: "toggle", documentId: "synth-doc-02" });
    state = reduceConsentSelection(state, { type: "preview", documentId: "synth-doc-02" });
    state = reduceConsentSelection(state, { type: "toggle", documentId: "synth-doc-02" });
    expect(isPreviewAvailable(state, "synth-doc-02")).toBe(false);
    expect(state.preview).toEqual({ status: "unavailable", reason: "selection-changed" });
  });

  it("ignores mutations that are not consented grants while ungranted or revoked", () => {
    const ungranted = initialConsentSelectionState;
    expect(reduceConsentSelection(ungranted, { type: "toggle", documentId: "synth-doc-01" })).toBe(ungranted);
    expect(reduceConsentSelection(ungranted, { type: "preview", documentId: "synth-doc-01" })).not.toBe(ungranted);
    const revoked = reduceConsentSelection(ungranted, { type: "revoke" });
    expect(reduceConsentSelection(revoked, { type: "toggle", documentId: "synth-doc-01" })).toBe(revoked);
    expect(reduceConsentSelection(revoked, { type: "preview", documentId: "synth-doc-01" }).preview).toEqual({
      status: "unavailable",
      reason: "consent-revoked",
    });
  });

  it("is deterministic: identical event sequences produce identical privacy state", () => {
    const events = [
      { type: "grant", documents: SYNTH_DOCS },
      { type: "toggle", documentId: "synth-doc-03" },
      { type: "preview", documentId: "synth-doc-03" },
      { type: "revoke" },
      { type: "grant", documents: SYNTH_DOCS },
    ] as const;
    const run = () => events.reduce((acc, event) => reduceConsentSelection(acc, event), initialConsentSelectionState);
    expect(run()).toEqual(run());
    expect(run().consent).toBe("granted");
    expect(run().preview).toEqual({ status: "none" });
    expect(selectedDocumentIds(run())).toEqual([]);
  });

  it("never exposes private identifiers in preview-facing or status strings", () => {
    let state = reduceConsentSelection(initialConsentSelectionState, { type: "grant", documents: SYNTH_DOCS });
    state = reduceConsentSelection(state, { type: "toggle", documentId: "synth-doc-01" });
    state = reduceConsentSelection(state, { type: "preview", documentId: "synth-doc-01" });
    state = reduceConsentSelection(state, { type: "revoke" });

    expect(state.preview.status === "granted" ? state.preview.token : "").not.toContain("synth-doc-");
    expect(consentSelectionLeakViolations(JSON.stringify(describeConsentSelectionUi(state)), SYNTH_DOCS)).toEqual([]);

    const leaked = consentSelectionLeakViolations(`opened synth-doc-02 for review`, SYNTH_DOCS);
    expect(leaked).toHaveLength(1);
    expect(leaked[0]).toContain("private document identifier");
  });

  it("notifies subscribers with the already-revoked snapshot (no stale frame)", () => {
    const store = createConsentSelectionStore();
    store.grantConsent(SYNTH_DOCS);
    store.toggleDocumentSelection("synth-doc-01");
    store.requestPreview("synth-doc-01");
    expect(isPreviewAvailable(store.getSnapshot(), "synth-doc-01")).toBe(true);

    const seen = vi.fn();
    const unsubscribe = store.subscribe(seen);
    store.revokeConsent();

    expect(seen).toHaveBeenCalledTimes(1);
    // The very first observation after the revoke dispatch is already clean.
    expect(selectedDocumentIds(store.getSnapshot())).toEqual([]);
    expect(store.getSnapshot().preview).toEqual({ status: "unavailable", reason: "consent-revoked" });
    unsubscribe();
    store.grantConsent(SYNTH_DOCS);
    expect(seen).toHaveBeenCalledTimes(1); // unsubscribed listeners hear nothing
  });

  it("grants nothing to unknown documents and de-duplicates the offer set", () => {
    const store = createConsentSelectionStore();
    store.grantConsent([...SYNTH_DOCS, SYNTH_DOCS[0]]);
    expect(store.getSnapshot().documents).toHaveLength(SYNTH_DOCS.length);
    store.toggleDocumentSelection("synth-doc-99");
    expect(store.getSnapshot().selection).toEqual({
      "synth-doc-01": false,
      "synth-doc-02": false,
      "synth-doc-03": false,
    });
    expect(grantedState().consent).toBe("granted");
  });
});
