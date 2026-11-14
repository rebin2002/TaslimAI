"use client";

/**
 * Issue #714 (Health Care) — bounded wiring of the #804 opt-in consent /
 * private-document selection panel into the #715 authenticated route.
 *
 * A signed-in user on `/create/health` can now:
 *  1. grant consent (explicit opt-in only — the initial state is inert);
 *  2. select one of the synthetic sample documents offered after the grant;
 *  3. revoke consent and immediately see the selection, the document rows and
 *     every preview affordance cleared in the same state transition;
 *  4. re-grant from a fully unselected list (no implicit restoration).
 *
 * Provider-free guarantees, pinned by `HealthCareConsentJourney.test.tsx`:
 *  - only invented synthetic fixtures exist; nothing is uploaded, read,
 *    stored, analyzed or sent to any provider — this file performs no
 *    network request, no API-route call and no browser-storage write;
 *  - consent state lives in memory for the mounted screen only; it is not
 *    persisted anywhere and never survives a session boundary;
 *  - synthetic document identifiers are never rendered into the screen;
 *  - the #804 controller/panel and the #715 route files are consumed as-is;
 *    nothing here re-implements or overwrites owner logic.
 */

import { useCallback, useMemo, useState } from "react";
import { useLocale } from "@/components/LocaleProvider";
import { HealthCareConsentSelectionPanel } from "@/components/HealthCareConsentSelectionPanel";
import {
  consentSelectionLeakViolations,
  createConsentSelectionStore,
  type ConsentSelectionStore,
  type PrivateDocument,
} from "@/lib/healthCareConsentSelection";
import {
  healthCareConsentJourneyCopy,
  healthCareConsentJourneyStrings,
} from "@/lib/healthCareConsentJourneyI18n";
import { healthCareDirection, healthCareLanguageTag } from "@/lib/healthCareI18n";
import type { Locale } from "@/lib/i18n";

export type HealthCareConsentJourneyProps = Readonly<{
  /** Overrides the active UI locale; used by deterministic tests only. */
  locale?: Locale;
}>;

export function HealthCareConsentJourney({ locale: localeOverride }: HealthCareConsentJourneyProps) {
  const { locale } = useLocale();
  const journeyLocale = localeOverride ?? locale;
  // One store per mount: closing or navigating away the screen discards the
  // consent state entirely — the journey never survives a session boundary.
  const [baseStore] = useState<ConsentSelectionStore>(() => createConsentSelectionStore());
  const copy = healthCareConsentJourneyCopy(journeyLocale);
  const strings = healthCareConsentJourneyStrings(journeyLocale);

  // Wraps only the grant action so the offered samples come from this
  // screen's feature-local fixtures. Every other affordance delegates to the
  // #804 store unchanged, preserving its revocation semantics exactly.
  const grantConsent = useCallback(
    (documents: readonly PrivateDocument[]) => {
      baseStore.grantConsent(documents.length > 0 ? documents : copy.documents);
    },
    [baseStore, copy.documents],
  );
  const store = useMemo<ConsentSelectionStore>(
    () => ({ ...baseStore, grantConsent }),
    [baseStore, grantConsent],
  );

  // Notices derived from the same synchronous snapshot the panel renders, so
  // a revocation clears them in the very same pass the rows disappear.
  const state = baseStore.getSnapshot();
  const grantedRows = state.documents.filter((doc) => state.selection[doc.id] === true);
  const showGrantedNotice = state.consent === "granted" && grantedRows.length > 0;
  const previewedDocumentId = state.preview.status === "granted" ? state.preview.documentId : null;
  const showPreviewNotice =
    state.consent === "granted" &&
    previewedDocumentId !== null &&
    grantedRows.some((doc) => doc.id === previewedDocumentId);

  return (
    <section
      className="health-consent-journey"
      dir={healthCareDirection(journeyLocale)}
      lang={healthCareLanguageTag(journeyLocale)}
      aria-labelledby="health-consent-journey-title"
    >
      <h2 id="health-consent-journey-title">{copy.heading}</h2>
      <p>{copy.intro}</p>
      <HealthCareConsentSelectionPanel store={store} strings={strings} />
      {showGrantedNotice ? <p role="status">{copy.grantedNotice}</p> : null}
      {showPreviewNotice ? <p role="status">{copy.previewNotice}</p> : null}
    </section>
  );
}

/**
 * Deterministic host-side guard re-used by the interaction tests: no
 * synthetic identifier may surface in journey-facing rendered output.
 */
export function journeyLeakViolations(locale: Locale, rendered: string): string[] {
  return consentSelectionLeakViolations(rendered, healthCareConsentJourneyCopy(locale).documents);
}
