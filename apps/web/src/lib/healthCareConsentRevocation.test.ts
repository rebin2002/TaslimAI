import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { HealthPlaceholderPage } from "@/components/HealthPlaceholderPage";
import { LocaleProvider } from "@/components/LocaleProvider";
import { healthCareCopy } from "@/lib/healthCareI18n";
import { locales, translations, type Locale } from "@/lib/i18n";

/**
 * Issue #714 — Health Care consent-revocation fence, stated against the placeholder
 * the application actually ships.
 *
 * Review 6005763472 (2026-11-21) found that the previous version of this file defined
 * its own `HealthCareUiState` and its own `revokeConsent()`, so its transition
 * assertions could not catch a regression in the application, and that its screen
 * guard rejected generic form controls (`<input`, `<form`, `<select`, checkboxes,
 * switches) even when they were unrelated to document previews. Both parts were
 * removed: this file drives the real component and the real dictionaries and keeps
 * only assertions that are true of the shipped UI, described as a placeholder.
 *
 * AUDIT CORRECTED AGAINST CURRENT MAIN (27ee26d2aab344f47bfa6510ea9f8553391e35f8).
 * The earlier version of this header claimed "no consent state exists in `apps/web`
 * at all". That claim is FALSE and is withdrawn. Real shared state now ships:
 * - `apps/web/src/lib/healthCareConsentSelection.ts` — the deterministic controller:
 *   `reduceConsentSelection(state, { type: "revoke" })` clears `selection`, sets
 *   `preview` to `{ status: "unavailable", reason: "consent-revoked" }`, bumps
 *   `revision` and `revocationCount`, all synchronously.
 * - `apps/web/src/components/HealthCareConsentSelectionPanel.tsx` — the real panel over
 *   that controller through `useSyncExternalStore`; when consent is not `active` it
 *   renders only the inert opt-in prompt plus a `role="status"` withdrawal notice.
 * - Both landed in commit `5d57f774` ("feat(health): add opt-in consent/private-document
 *   selection controller (#714 slice)") and reached main through integration PR #1233
 *   (merged 2026-11-22T10:06:39Z), after this file's original audit SHA `7322c561`.
 *
 * The real revocation TRANSITION is therefore already covered by tests that ship on
 * main and are not repeated here:
 * - `healthCareConsentSelection.test.ts:63` "clears all selections and disables
 *   previews immediately on revocation" (plus `:86` no implicit restore on re-grant,
 *   `:102` fail-closed resume while revoked, `:153` mutations ignored while revoked).
 * - `HealthCareConsentSelectionPanel.test.tsx:86` "clears every selection and removes
 *   preview affordances immediately on withdrawal", driven through the same store the
 *   browser uses.
 * Restating those assertions in this file would be an already-shipped duplicate, so
 * this lane keeps only what main does NOT pin.
 *
 * What is still true, and what this file pins:
 * 1. The health routes are still placeholders: `/personal/health` and `/create/health`
 *    each render `HealthPlaceholderPage` (`apps/web/src/app/{personal,create}/health/page.tsx`),
 *    and nothing outside tests imports `HealthCareConsentSelectionPanel`. The real
 *    consent UI is therefore not reachable by a user yet, so no consent control can be
 *    granted, toggled or revoked from the shipped routes.
 * 2. Every control the placeholder renders is disabled and self-labels as unavailable,
 *    so no health-document action — the action a revocation would have to guard —
 *    can be taken from it.
 * 3. The placeholder mounts NO consent surface: its markup never contains the real
 *    panel's own DOM signature (`health-consent-selection`), so the informational
 *    screen cannot silently acquire a half-wired consent control. This is additive to
 *    (1) and (2) above and is asserted against the real panel's markup contract.
 * 4. Nothing the placeholder renders reads as a consent grant, revocation or toggle.
 * 5. Its rendered privacy note denies collecting, uploading, storing or analyzing
 *    health information, which is the current truth a revocation must not contradict.
 * 6. In every shipped locale the planned actions stay labelled unavailable over an
 *    unchanged feature set, so no locale ships a half-enabled revocable surface.
 * 7. The shipped product promise that a future health feature requires explicit
 *    consent and clear workspace ownership controls is still present in the real
 *    translation dictionary — the requirement a future revocation flow must satisfy.
 *
 * Remaining acceptance gap, stated rather than papered over: an END-TO-END user
 * revocation transition (grant in a real screen, revoke, observe the cleared state
 * through the route) cannot be tested until the consent panel is mounted on a health
 * route. When that routing lands, assertion 3 and assertion 4 are designed to fail and
 * must be replaced by a transition test through the mounted component, not by a model
 * living in this file.
 *
 * Test-only, synthetic-free: edits no source file, claims no owner-held path, mounts no
 * provider, and makes no API, model, storage or network call.
 */

/** The real panel's own DOM signature, copied from `HealthCareConsentSelectionPanel.tsx`. */
const CONSENT_PANEL_DOM_SIGNATURE = "health-consent-selection";

function renderPlaceholder(entry?: "create" | "personal"): string {
  return renderToStaticMarkup(
    createElement(
      LocaleProvider,
      null,
      createElement(HealthPlaceholderPage, entry === undefined ? {} : { entry }),
    ),
  );
}

function renderedButtons(html: string): { tag: string; body: string }[] {
  const out: { tag: string; body: string }[] = [];
  const re = /<button\b([^>]*)>([\s\S]*?)<\/button>/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(html)) !== null) out.push({ tag: m[1], body: m[2] });
  return out;
}

const visibleText = (markup: string) => markup.replace(/<[^>]*>/g, "").replace(/\s+/g, " ").trim();

describe("issue #714 Health Care consent-revocation fence on the shipped placeholder", () => {
  it("renders no enabled action for health documents, in either entry surface", () => {
    for (const entry of ["personal", "create"] as const) {
      const html = renderPlaceholder(entry);
      expect(html).toContain('id="health-care-title"');
      const buttons = renderedButtons(html);
      // The planned actions are exactly the feature list; none is an enabled control.
      expect(buttons.length, `${entry}: every planned action is rendered`).toBe(
        healthCareCopy("en").features.length,
      );
      for (const { tag, body } of buttons) {
        expect(tag, "planned action must not be actionable").toContain("disabled");
        expect(body).toContain(healthCareCopy("en").actionUnavailable);
      }
      expect(html).not.toMatch(/<button\b(?![^>]*\bdisabled)/);
    }
  });

  it("labels the documents-and-reports action as unavailable instead of a preview affordance", () => {
    const html = renderPlaceholder("create");
    const en = healthCareCopy("en");
    const documents = en.features.find((feature) => feature.id === "reports");
    expect(documents, "the health-document action is the surface revocation would guard").toBeDefined();
    const button = renderedButtons(html).find(({ body }) => visibleText(body).includes(documents!.label));
    expect(button, "the health-document action is rendered").toBeDefined();
    expect(button!.tag).toContain("disabled");
    expect(button!.tag).toContain('aria-describedby="health-coming-soon"');
    expect(visibleText(button!.body)).toContain(en.actionUnavailable);
  });

  it("mounts no consent surface at all on the shipped placeholder screens", () => {
    // Additive to the affordance scan below: the real panel always renders a section
    // whose class and aria ids contain "health-consent-selection". Its absence proves
    // the placeholder has not quietly started mounting the consent controller UI while
    // still describing itself as informational. The placeholder legitimately renders its
    // own `role="status"` "not yet available" line, so that is not treated as a leak.
    for (const entry of ["personal", "create"] as const) {
      const html = renderPlaceholder(entry);
      expect(html, `${entry}: placeholder must not mount the real consent panel`)
        .not.toContain(CONSENT_PANEL_DOM_SIGNATURE);
      expect(html, `${entry}: no document-selection affordance from the real panel`)
        .not.toContain("health-consent-selection-documents");
    }
  });

  it("presents no consent grant, toggle or revocation surface in the real UI", () => {
    for (const entry of ["personal", "create"] as const) {
      const text = visibleText(renderPlaceholder(entry)).toLowerCase();
      for (const affordance of ["consent", "grant", "revoke", "allow access"]) {
        expect(text, `${entry}: placeholder must not imply a ${affordance} control`).not.toContain(affordance);
      }
    }
  });

  it("keeps the rendered privacy note a denial of health-data handling", () => {
    const html = renderPlaceholder("personal");
    const note = healthCareCopy("en").privacyNote;
    expect(html).toContain(note);
    expect(note).toMatch(/does not collect, upload, store, or analyze health information/);
    expect(note).toMatch(/sends no health information to external services/);
  });

  it("ships no half-enabled revocable surface in any supported locale", () => {
    const enIds = healthCareCopy("en").features.map((feature) => feature.id).sort();
    for (const locale of locales as readonly Locale[]) {
      const copy = healthCareCopy(locale);
      expect(copy.features.map((feature) => feature.id).sort(), `${locale}: same planned surfaces`).toEqual(enIds);
      expect(copy.actionUnavailable.trim().length, `${locale}: actions are labelled unavailable`).toBeGreaterThan(0);
      expect(copy.status.trim().length, `${locale}: the not-yet-available status is stated`).toBeGreaterThan(0);
      for (const feature of copy.features) {
        expect(feature.label.trim().length, `${locale}: ${feature.id} label is present`).toBeGreaterThan(0);
      }
    }
  });

  it("keeps the explicit-consent precondition of a future health feature truthful in the real dictionary", () => {
    const enNote = translations.en["health.privacyNote"];
    expect(enNote, "shipped health privacy note exists").toBeTruthy();
    expect(enNote).toContain("No health information is requested or saved");
    expect(enNote).toContain("it will require explicit consent and clear workspace ownership controls");
    for (const locale of locales as readonly Locale[]) {
      expect(
        translations[locale]["health.privacyNote"]?.trim().length,
        `${locale}: the consent precondition is not dropped`,
      ).toBeGreaterThan(0);
    }
  });
});
