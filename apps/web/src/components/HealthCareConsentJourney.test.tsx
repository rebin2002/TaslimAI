/**
 * Issue #714 (Health Care) — provider-free interaction tests for the
 * #715 route x #804 consent-panel wiring.
 *
 * Synthetic fixtures only (opaque ids, invented sample labels): no real
 * health data, no uploads, no API routes, no provider or model calls, no
 * network, no persistence. Rendered with `react-dom/server`, matching the
 * repo's Node-test convention used by the #715 and #804 suites.
 */

import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { ConsentSelectionView } from "./HealthCareConsentSelectionPanel";
import { HealthCareConsentJourney, journeyLeakViolations } from "./HealthCareConsentJourney";
import { LocaleProvider } from "./LocaleProvider";
import {
  createConsentSelectionStore,
  type ConsentSelectionState,
  type ConsentSelectionStore,
} from "@/lib/healthCareConsentSelection";
import {
  HEALTH_CARE_SYNTHETIC_DOCUMENT_IDS,
  healthCareConsentJourneyCopy,
  healthCareConsentJourneyStrings,
} from "@/lib/healthCareConsentJourneyI18n";
import { locales, type Locale } from "@/lib/i18n";

function view(locale: Locale, state: ConsentSelectionState, store: ConsentSelectionStore) {
  return renderToStaticMarkup(
    <ConsentSelectionView state={state} strings={healthCareConsentJourneyStrings(locale)} store={store} />,
  );
}

/** Mirrors the host wiring: a bare grant offers this screen's samples. */
function journeyStore(locale: Locale): ConsentSelectionStore {
  const base = createConsentSelectionStore();
  const documents = healthCareConsentJourneyCopy(locale).documents;
  return {
    ...base,
    grantConsent: (offered) => base.grantConsent(offered.length > 0 ? offered : documents),
  };
}

describe("HealthCareConsentJourney wiring (#715 route x #804 panel, synthetic, #714)", () => {
  it("starts inert: an opt-in prompt only, in every supported locale", () => {
    for (const locale of locales) {
      const store = journeyStore(locale);
      const html = view(locale, store.getSnapshot(), store);
      const copy = healthCareConsentJourneyCopy(locale);
      expect(html).toContain(copy.optInTitle);
      expect(html).toContain(copy.optInBody);
      expect(html).toContain(copy.grantLabel);
      // Nothing private-ish before the explicit opt-in: no document rows,
      // no selection toggles, no preview affordances, no withdraw button.
      expect(html).not.toContain("aria-pressed");
      expect(journeyLeakViolations(locale, html)).toEqual([]);
      for (const document of copy.documents) {
        expect(html).not.toContain(document.label);
      }
    }
  });

  it("grants consent and lists the synthetic samples fully unselected", () => {
    const locale: Locale = "en";
    const store = journeyStore(locale);
    store.grantConsent([]);
    const html = view(locale, store.getSnapshot(), store);
    const copy = healthCareConsentJourneyCopy(locale);
    expect(html).toContain(copy.activeTitle);
    expect(html).toContain(copy.documents[0].label);
    expect(html).toContain(copy.documents[1].label);
    expect(html.match(/aria-pressed="false"/g)).toHaveLength(2);
    expect(html).not.toContain('aria-pressed="true"');
    // A grant is not permission to preview: both preview affordances disabled.
    expect(html.match(/disabled=""[^>]*>Open preview</g)).toHaveLength(2);
  });

  it("walks the full journey: select, preview, revoke, cleared state", () => {
    const locale: Locale = "en";
    const store = journeyStore(locale);
    const copy = healthCareConsentJourneyCopy(locale);

    store.grantConsent([]);
    store.toggleDocumentSelection("synth-journey-01");
    let state = store.getSnapshot();
    expect(state.selection["synth-journey-01"]).toBe(true);
    let html = view(locale, state, store);
    expect(html).toContain('aria-pressed="true"');
    // Two-step preview: selection alone keeps both affordances disabled.
    expect(html.match(/disabled=""[^>]*>Open preview</g)).toHaveLength(2);

    store.requestPreview("synth-journey-01");
    state = store.getSnapshot();
    expect(state.preview.status).toBe("granted");
    html = view(locale, state, store);
    expect(html.match(/disabled=""[^>]*>Open preview</g)).toHaveLength(1);

    // Revocation clears selection, rows and previews in one synchronous pass.
    store.revokeConsent();
    state = store.getSnapshot();
    expect(state.consent).toBe("revoked");
    expect(state.selection).toEqual({});
    expect(state.preview.status).toBe("unavailable");
    html = view(locale, state, store);
    expect(html).toContain(copy.optInTitle);
    expect(html).toContain(copy.withdrawnNotice);
    expect(html).not.toContain(copy.documents[0].label);
    expect(html).not.toContain(copy.documents[1].label);
    expect(html).not.toContain(copy.previewLabel);
    expect(html).not.toContain(copy.revokeLabel);
    expect(html).not.toContain("aria-pressed");

    // Re-grant re-offers the samples unselected — no implicit restoration.
    store.grantConsent([]);
    html = view(locale, store.getSnapshot(), store);
    expect(html.match(/aria-pressed="false"/g)).toHaveLength(2);
    expect(html).not.toContain('aria-pressed="true"');
  });

  it("localizes the journey screen with RTL attributes for Arabic and Sorani", () => {
    const cases: ReadonlyArray<{ locale: Locale; dir: string; lang: string }> = [
      { locale: "en", dir: "ltr", lang: "en" },
      { locale: "ar", dir: "rtl", lang: "ar" },
      { locale: "ku", dir: "rtl", lang: "ckb" },
    ];
    for (const { locale, dir, lang } of cases) {
      const html = renderToStaticMarkup(
        <LocaleProvider>
          <HealthCareConsentJourney locale={locale} />
        </LocaleProvider>,
      );
      const copy = healthCareConsentJourneyCopy(locale);
      expect(html).toContain(`dir="${dir}" lang="${lang}"`);
      expect(html).toContain(copy.heading);
      expect(html).toContain(copy.intro);
      expect(html).toContain(copy.optInTitle);
      expect(html).toContain(copy.grantLabel);
      // No document rows before consent, in any locale.
      for (const document of copy.documents) {
        expect(html).not.toContain(document.label);
      }
      // Identifier leak guard over the whole composed screen.
      expect(journeyLeakViolations(locale, html)).toEqual([]);
    }
  });

  it("keeps the copy sets complete and parallel across English, Arabic and Sorani", () => {
    const en = healthCareConsentJourneyCopy("en");
    for (const locale of locales) {
      const copy = healthCareConsentJourneyCopy(locale);
      const required = [
        copy.heading,
        copy.intro,
        copy.optInTitle,
        copy.optInBody,
        copy.grantLabel,
        copy.revokeLabel,
        copy.activeTitle,
        copy.withdrawnNotice,
        copy.previewLabel,
        copy.grantedNotice,
        copy.previewNotice,
      ];
      expect(required.every((value) => value.trim().length > 0), `copy complete for ${locale}`).toBe(true);
      expect(copy.documents.map((document) => document.id)).toEqual(
        [...HEALTH_CARE_SYNTHETIC_DOCUMENT_IDS],
      );
      expect(copy.documents.every((document) => document.label.trim().length > 0)).toBe(true);
      // Synthetic ids are stable across locales so labels stay aligned.
      if (locale !== "en") {
        expect(copy.documents.map((document) => document.id)).toEqual(en.documents.map((document) => document.id));
      }
    }
  });

  it("drops a resume proposal while consent is not granted (fail-closed)", () => {
    const locale: Locale = "ku";
    const base = createConsentSelectionStore();
    base.resumeSession({ previouslySelectedDocumentIds: ["synth-journey-01"] });
    const state = base.getSnapshot();
    expect(state.consent).toBe("ungranted");
    expect(state.selection).toEqual({});
    expect(state.droppedResumeSelections).toBe(1);
    const html = view(locale, state, base);
    expect(html).toContain(healthCareConsentJourneyCopy(locale).optInTitle);
  });

  it("audits the wiring sources for provider, upload and persistence surfaces", () => {
    const sources = [
      ["HealthCareConsentJourney.tsx", new URL("./HealthCareConsentJourney.tsx", import.meta.url)],
      ["app/create/health/page.tsx", new URL("../app/create/health/page.tsx", import.meta.url)],
      ["healthCareConsentJourneyI18n.ts", new URL("../lib/healthCareConsentJourneyI18n.ts", import.meta.url)],
    ] as const;
    const banned = ["fetch(", "/api/", "localStorage", "sessionStorage", "document.cookie", "XMLHttpRequest", "<input", "<form", "type=\"file\""];
    for (const [name, url] of sources) {
      const source = readFileSync(url, "utf8");
      for (const surface of banned) {
        expect(source, `${name} must not contain ${surface}`).not.toContain(surface);
      }
    }
  });
});
