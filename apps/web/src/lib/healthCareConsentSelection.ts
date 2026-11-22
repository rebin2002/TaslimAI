/**
 * Issue #714 (Health Care) — opt-in client consent and private-document
 * selection state controller.
 *
 * Privacy-first guarantees pinned by `healthCareConsentSelection.test.ts`:
 *  1. Nothing is consented, selected or previewable until the user grants
 *     consent explicitly; the initial state is inert.
 *  2. `revokeConsent()` clears every private-document selection and disables
 *     previews *immediately*, in the same synchronous state transition.
 *  3. Re-granting consent never implicitly restores a previous selection:
 *     documents are re-offered in the unselected state and only an explicit
 *     `toggleDocumentSelection()` re-selects one. A carry-over resume
 *     proposal is dropped whenever consent is not currently granted.
 *  4. The controller holds opaque synthetic identifiers and caller-provided
 *     display labels only. It never touches storage, network, upload, model
 *     or any other collecting API, and performs no medical analysis.
 *
 * Deliberately free of i18n/navigation imports so it can be adopted by any
 * Health Care screen once the owner-claimed route files (#715/#793) settle.
 */

export type ConsentStatus = "ungranted" | "granted" | "revoked";

export type PrivateDocumentKind =
  | "lab-report"
  | "imaging"
  | "prescription"
  | "note"
  | "other";

export type PrivateDocument = {
  /** Opaque synthetic identifier; never derived from real patient data. */
  id: string;
  /** Caller-provided short label; the controller never inspects document contents. */
  label: string;
  kind: PrivateDocumentKind;
};

export type PreviewUnavailableReason =
  | "no-consent"
  | "not-selected"
  | "consent-revoked"
  | "selection-changed";

export type PreviewState =
  | { status: "none" }
  | { status: "unavailable"; reason: PreviewUnavailableReason }
  | { status: "granted"; token: string; documentId: string };

export type ResumeProposal = {
  /**
   * Selection carried over from an earlier view of the session. Honoured only
   * while consent is currently granted; otherwise it is dropped entirely.
   */
  previouslySelectedDocumentIds: readonly string[];
};

export type ConsentSelectionState = {
  consent: ConsentStatus;
  /** Documents the user consented to have offered; empty until first grant. */
  documents: readonly PrivateDocument[];
  /** documentId -> selected. Cleared entirely on revocation. */
  selection: Readonly<Record<string, boolean>>;
  preview: PreviewState;
  /** Bumped on every privacy-relevant transition; invalidates old tokens. */
  revision: number;
  /** Monotonic count of revocations, for UI acknowledgement messaging. */
  revocationCount: number;
  /** Count of resume proposals ignored because consent was not granted. */
  droppedResumeSelections: number;
};

export type ConsentSelectionEvent =
  | { type: "grant"; documents: readonly PrivateDocument[] }
  | { type: "revoke" }
  | { type: "toggle"; documentId: string }
  | { type: "preview"; documentId: string }
  | { type: "resume"; proposal?: ResumeProposal };

export const initialConsentSelectionState: ConsentSelectionState = {
  consent: "ungranted",
  documents: [],
  selection: {},
  preview: { status: "none" },
  revision: 0,
  revocationCount: 0,
  droppedResumeSelections: 0,
};

export function selectedDocumentIds(state: ConsentSelectionState): string[] {
  return state.consent === "granted"
    ? state.documents.filter((doc) => state.selection[doc.id] === true).map((doc) => doc.id)
    : [];
}

export function isDocumentSelected(state: ConsentSelectionState, documentId: string): boolean {
  return state.consent === "granted" && state.selection[documentId] === true;
}

export function isPreviewAvailable(state: ConsentSelectionState, documentId: string): boolean {
  return (
    state.consent === "granted" &&
    isDocumentSelected(state, documentId) &&
    state.preview.status === "granted" &&
    state.preview.documentId === documentId
  );
}

function unavailablePreview(reason: PreviewUnavailableReason): PreviewState {
  return { status: "unavailable", reason };
}

function dedupeDocuments(documents: readonly PrivateDocument[]): PrivateDocument[] {
  const seen = new Set<string>();
  const out: PrivateDocument[] = [];
  for (const doc of documents) {
    if (!doc || typeof doc.id !== "string" || doc.id.length === 0) continue;
    if (seen.has(doc.id)) continue;
    seen.add(doc.id);
    out.push({
      id: doc.id,
      label: typeof doc.label === "string" && doc.label.length > 0 ? doc.label : doc.id,
      kind: doc.kind ?? "other",
    });
  }
  return out;
}

/**
 * Deterministic reducer for the consent/selection lifecycle. Every transition
 * is synchronous, so revocation takes effect before any caller can observe
 * the previous state — no async flush, no stale preview.
 */
export function reduceConsentSelection(
  state: ConsentSelectionState,
  event: ConsentSelectionEvent,
): ConsentSelectionState {
  switch (event.type) {
    case "grant": {
      const documents = dedupeDocuments(event.documents);
      const selection: Record<string, boolean> = {};
      // Invariant 3: re-granting always starts from an entirely unselected set.
      for (const doc of documents) selection[doc.id] = false;
      return {
        ...state,
        consent: "granted",
        documents,
        selection,
        preview: { status: "none" },
        revision: state.revision + 1,
      };
    }

    case "revoke": {
      // Invariant 2: selection cleared and previews disabled in the same tick,
      // whatever the previous status was (including a redundant revoke).
      return {
        ...state,
        consent: "revoked",
        selection: {},
        preview: unavailablePreview("consent-revoked"),
        revision: state.revision + 1,
        revocationCount: state.revocationCount + 1,
      };
    }

    case "toggle": {
      if (state.consent !== "granted") return state;
      if (!state.documents.some((doc) => doc.id === event.documentId)) return state;
      const nextSelected = state.selection[event.documentId] !== true;
      const selection = { ...state.selection, [event.documentId]: nextSelected };
      const previewCleared =
        state.preview.status === "granted" && state.preview.documentId === event.documentId;
      // A preview must never outlive the selection it belongs to.
      return {
        ...state,
        selection,
        preview: previewCleared ? unavailablePreview("selection-changed") : state.preview,
        revision: state.revision + 1,
      };
    }

    case "preview": {
      if (state.consent !== "granted") {
        return {
          ...state,
          preview: unavailablePreview(state.consent === "revoked" ? "consent-revoked" : "no-consent"),
        };
      }
      if (!isDocumentSelected(state, event.documentId)) {
        return { ...state, preview: unavailablePreview("not-selected") };
      }
      const revision = state.revision + 1;
      return {
        ...state,
        preview: { status: "granted", documentId: event.documentId, token: `preview-${revision}` },
        revision,
      };
    }

    case "resume": {
      const proposed = event.proposal?.previouslySelectedDocumentIds ?? [];
      if (proposed.length === 0) return state;
      if (state.consent !== "granted") {
        // Fail-closed resume (invariant 3): a carry-over selection is honoured
        // by nothing less than currently-granted consent; it is dropped.
        return {
          ...state,
          selection: {},
          preview: unavailablePreview(state.consent === "revoked" ? "consent-revoked" : "no-consent"),
          droppedResumeSelections: state.droppedResumeSelections + proposed.length,
          revision: state.revision + 1,
        };
      }
      // Already granted: only selections the user still holds survive a
      // resume; anything else stays unselected. No silent re-selection.
      const stillValid = proposed.filter((id) => state.selection[id] === true).length;
      if (stillValid === proposed.length) return state;
      return { ...state, revision: state.revision + (proposed.length - stillValid) };
    }

    default:
      return state;
  }
}

/**
 * Read-only guard used by tests and host screens: private identifiers must
 * never appear in preview-facing strings or serialized consent output.
 */
export function consentSelectionLeakViolations(
  serialized: string,
  documents: readonly PrivateDocument[],
): string[] {
  const violations: string[] = [];
  for (const doc of documents) {
    if (doc.id && serialized.includes(doc.id)) {
      violations.push(`private document identifier "${doc.id}" leaked into serialized consent output`);
    }
  }
  return violations;
}

/**
 * Pure presentation mapping. Host screens own their localisation; the
 * controller only decides *what is safe to show*, never what it says.
 */
export type ConsentSelectionUiSnapshot = {
  consentLabel: "opt-in" | "active" | "withdrawn";
  canOfferDocuments: boolean;
  canPreviewAny: boolean;
  rows: ReadonlyArray<{
    documentId: string;
    selected: boolean;
    toggleEnabled: boolean;
    previewEnabled: boolean;
  }>;
};

export function describeConsentSelectionUi(state: ConsentSelectionState): ConsentSelectionUiSnapshot {
  const granted = state.consent === "granted";
  return {
    consentLabel: granted ? "active" : state.consent === "revoked" ? "withdrawn" : "opt-in",
    canOfferDocuments: granted,
    canPreviewAny: granted && selectedDocumentIds(state).length > 0,
    rows: granted
      ? state.documents.map((doc) => ({
          documentId: doc.id,
          selected: state.selection[doc.id] === true,
          toggleEnabled: true,
          previewEnabled: isPreviewAvailable(state, doc.id),
        }))
      : [],
  };
}

/**
 * Subscribe-friendly store around the reducer, ready for
 * `useSyncExternalStore` in a React host without the controller itself
 * depending on React.
 */
export type ConsentSelectionStore = {
  getSnapshot: () => ConsentSelectionState;
  subscribe: (listener: () => void) => () => void;
  grantConsent: (documents: readonly PrivateDocument[]) => void;
  revokeConsent: () => void;
  toggleDocumentSelection: (documentId: string) => void;
  requestPreview: (documentId: string) => void;
  resumeSession: (proposal?: ResumeProposal) => void;
};

export function createConsentSelectionStore(
  initial: ConsentSelectionState = initialConsentSelectionState,
): ConsentSelectionStore {
  let state = initial;
  const listeners = new Set<() => void>();

  const dispatch = (event: ConsentSelectionEvent) => {
    const next = reduceConsentSelection(state, event);
    if (next === state) return;
    state = next;
    for (const listener of [...listeners]) listener();
  };

  return {
    getSnapshot: () => state,
    subscribe: (listener) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    grantConsent: (documents) => dispatch({ type: "grant", documents }),
    revokeConsent: () => dispatch({ type: "revoke" }),
    toggleDocumentSelection: (documentId) => dispatch({ type: "toggle", documentId }),
    requestPreview: (documentId) => dispatch({ type: "preview", documentId }),
    resumeSession: (proposal) => dispatch({ type: "resume", proposal }),
  };
}
