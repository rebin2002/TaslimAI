"use client";

/**
 * Issue #714 (Health Care) — reusable opt-in consent / private-document
 * selection panel.
 *
 * Presentational wrapper over `healthCareConsentSelection` (the state
 * controller). It renders *only* what the controller declares safe:
 *  - no document rows at all until consent is granted;
 *  - preview affordances only for an explicitly selected document;
 *  - every affordance disappears synchronously when consent is withdrawn.
 *
 * It performs no fetching, uploading, storing or model calls and takes its
 * copy (localisation) from the host screen, so it never depends on the
 * owner-claimed i18n.ts or navigation.ts.
 */

import { useCallback, useMemo, useSyncExternalStore } from "react";
import {
  describeConsentSelectionUi,
  type ConsentSelectionState,
  type ConsentSelectionStore,
} from "@/lib/healthCareConsentSelection";

export type HealthCareConsentSelectionPanelStrings = {
  /** Heading for the opt-in prompt shown before consent is granted. */
  optInTitle: string;
  /** Body copy for the opt-in prompt. */
  optInBody: string;
  /** Label of the button that grants consent. */
  grantLabel: string;
  /** Label of the button that withdraws consent. */
  revokeLabel: string;
  /** Heading shown once consent is active. */
  activeTitle: string;
  /** Notice shown once consent has been withdrawn. */
  withdrawnNotice: string;
  /** Label of the per-document preview affordance. */
  previewLabel: string;
};

export type ConsentSelectionViewProps = Readonly<{
  state: ConsentSelectionState;
  strings: HealthCareConsentSelectionPanelStrings;
  store: Pick<
    ConsentSelectionStore,
    "grantConsent" | "revokeConsent" | "toggleDocumentSelection" | "requestPreview"
  >;
}>;

export type HealthCareConsentSelectionPanelProps = Readonly<{
  store: ConsentSelectionStore;
  strings: HealthCareConsentSelectionPanelStrings;
}>;

export function HealthCareConsentSelectionPanel({ store, strings }: HealthCareConsentSelectionPanelProps) {
  const subscribe = useCallback((onChange: () => void) => store.subscribe(onChange), [store]);
  const getSnapshot = useCallback(() => store.getSnapshot(), [store]);
  const state = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  return <ConsentSelectionView state={state} strings={strings} store={store} />;
}

/** Pure render entry point; also used by the deterministic static markup tests. */
export function ConsentSelectionView({ state, strings, store }: ConsentSelectionViewProps) {
  const ui = describeConsentSelectionUi(state);
  const labels = useMemo(() => {
    const map = new Map<string, string>();
    for (const doc of state.documents) map.set(doc.id, doc.label);
    return map;
  }, [state.documents]);

  if (ui.consentLabel !== "active") {
    // Inert prompt: shown for both the first visit and after withdrawal.
    // The documents the user consented to have offered are never listed
    // here, so a withdrawal leaves nothing private-ish on screen.
    return (
      <section className="health-consent-selection" aria-labelledby="health-consent-selection-title">
        <h2 id="health-consent-selection-title">{strings.optInTitle}</h2>
        <p>{strings.optInBody}</p>
        {ui.consentLabel === "withdrawn" ? <p role="status">{strings.withdrawnNotice}</p> : null}
        <button type="button" onClick={() => store.grantConsent(state.documents)}>
          {strings.grantLabel}
        </button>
      </section>
    );
  }

  return (
    <section className="health-consent-selection" aria-labelledby="health-consent-selection-title">
      <h2 id="health-consent-selection-title">{strings.activeTitle}</h2>
      <ul className="health-consent-selection-documents">
        {ui.rows.map((row) => (
          <li key={row.documentId}>
            <button
              type="button"
              aria-pressed={row.selected}
              onClick={() => store.toggleDocumentSelection(row.documentId)}
            >
              {labels.get(row.documentId) ?? row.documentId}
            </button>
            <button
              type="button"
              disabled={!row.previewEnabled}
              onClick={() => store.requestPreview(row.documentId)}
            >
              {strings.previewLabel}
            </button>
          </li>
        ))}
      </ul>
      <button type="button" onClick={() => store.revokeConsent()}>
        {strings.revokeLabel}
      </button>
    </section>
  );
}
