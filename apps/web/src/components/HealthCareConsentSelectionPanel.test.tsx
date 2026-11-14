/**
 * Issue #714 (Health Care) — deterministic synthetic tests for the reusable
 * consent / private-document selection panel.
 *
 * Synthetic fixtures only (opaque ids, invented labels, no medical content).
 * Rendered with `react-dom/server` in the repo's default Node test
 * environment, matching the existing component-test convention; the
 * interactive affordances are driven through the same controller store the
 * browser uses, so "revocation clears the screen" is verified end to end.
 */

import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import {
  ConsentSelectionView,
  type HealthCareConsentSelectionPanelStrings,
} from "./HealthCareConsentSelectionPanel";
import {
  createConsentSelectionStore,
  type PrivateDocument,
} from "@/lib/healthCareConsentSelection";

const SYNTH_DOCS: PrivateDocument[] = [
  { id: "synth-doc-01", label: "Synthetic Panel A", kind: "lab-report" },
  { id: "synth-doc-02", label: "Synthetic Panel B", kind: "imaging" },
];

const STRINGS: HealthCareConsentSelectionPanelStrings = {
  optInTitle: "Use your own health documents",
  optInBody: "Nothing is read until you choose a document.",
  grantLabel: "Enable private documents",
  revokeLabel: "Withdraw consent",
  activeTitle: "Private documents",
  withdrawnNotice: "Consent withdrawn. Selections were cleared.",
  previewLabel: "Open preview",
};

describe("HealthCareConsentSelectionPanel (synthetic, #714)", () => {
  it("offers only the opt-in prompt before consent exists", () => {
    const store = createConsentSelectionStore();
    const html = renderToStaticMarkup(
      <ConsentSelectionView state={store.getSnapshot()} strings={STRINGS} store={store} />,
    );
    expect(html).toContain(STRINGS.optInTitle);
    expect(html).toContain(STRINGS.grantLabel);
    // Nothing private-ish and no preview affordance is on screen.
    expect(html).not.toContain("Synthetic Panel");
    expect(html).not.toContain("synth-doc-");
    expect(html).not.toContain(STRINGS.previewLabel);
    expect(html).not.toContain(STRINGS.revokeLabel);
  });

  it("lists documents unselected and keeps previews disabled after an explicit grant", () => {
    const store = createConsentSelectionStore();
    store.grantConsent(SYNTH_DOCS);
    const html = renderToStaticMarkup(
      <ConsentSelectionView state={store.getSnapshot()} strings={STRINGS} store={store} />,
    );
    expect(html).toContain(STRINGS.activeTitle);
    expect(html.match(/aria-pressed="false"/g)).toHaveLength(SYNTH_DOCS.length);
    expect(html).not.toContain('aria-pressed="true"');
    // Grant is not consent to preview: every preview button is disabled.
    expect(html.match(/disabled=""[^>]*>Open preview</g)).toHaveLength(SYNTH_DOCS.length);
    expect(html).toContain(STRINGS.revokeLabel);
  });

  it("requires an explicit open: selection alone never enables a preview", () => {
    const store = createConsentSelectionStore();
    store.grantConsent(SYNTH_DOCS);
    store.toggleDocumentSelection("synth-doc-02");
    const afterSelect = renderToStaticMarkup(
      <ConsentSelectionView state={store.getSnapshot()} strings={STRINGS} store={store} />,
    );
    expect(afterSelect).toContain('aria-pressed="true"');
    // Both preview affordances stay disabled until the user opens one.
    expect(afterSelect.match(/disabled=""[^>]*>Open preview</g)).toHaveLength(SYNTH_DOCS.length);

    store.requestPreview("synth-doc-02");
    const afterOpen = renderToStaticMarkup(
      <ConsentSelectionView state={store.getSnapshot()} strings={STRINGS} store={store} />,
    );
    expect(afterOpen.match(/disabled=""[^>]*>Open preview</g)).toHaveLength(1);
    expect(afterOpen).not.toContain("synth-doc-");
  });

  it("clears every selection and removes preview affordances immediately on withdrawal", () => {
    const store = createConsentSelectionStore();
    store.grantConsent(SYNTH_DOCS);
    store.toggleDocumentSelection("synth-doc-01");
    store.requestPreview("synth-doc-01");
    store.revokeConsent();

    const html = renderToStaticMarkup(
      <ConsentSelectionView state={store.getSnapshot()} strings={STRINGS} store={store} />,
    );
    expect(html).toContain(STRINGS.optInTitle);
    expect(html).toContain(STRINGS.withdrawnNotice);
    expect(html).not.toContain("Synthetic Panel");
    expect(html).not.toContain("synth-doc-");
    expect(html).not.toContain(STRINGS.previewLabel);
    expect(html).not.toContain(STRINGS.revokeLabel);
    expect(html).not.toContain('aria-pressed');
  });

  it("re-grants to a fully unselected list rather than restoring the prior choice", () => {
    const store = createConsentSelectionStore();
    store.grantConsent(SYNTH_DOCS);
    store.toggleDocumentSelection("synth-doc-01");
    store.revokeConsent();
    store.grantConsent(SYNTH_DOCS);

    const html = renderToStaticMarkup(
      <ConsentSelectionView state={store.getSnapshot()} strings={STRINGS} store={store} />,
    );
    expect(html).toContain(STRINGS.activeTitle);
    expect(html).not.toContain('aria-pressed="true"');
    expect(html.match(/aria-pressed="false"/g)).toHaveLength(SYNTH_DOCS.length);
    expect(html.match(/disabled=""/g)).toHaveLength(SYNTH_DOCS.length);
  });

  it("exposes an accessible consent state container in every phase", () => {
    const store = createConsentSelectionStore();
    for (const phase of ["opt-in", "granted", "revoked"] as const) {
      if (phase === "granted") store.grantConsent(SYNTH_DOCS);
      if (phase === "revoked") store.revokeConsent();
      const html = renderToStaticMarkup(
        <ConsentSelectionView state={store.getSnapshot()} strings={STRINGS} store={store} />,
      );
      expect(html).toContain('aria-labelledby="health-consent-selection-title"');
      expect(html).toContain("<h2");
    }
  });
});
