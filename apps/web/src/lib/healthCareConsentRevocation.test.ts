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
 * switches) even when they were unrelated to document previews. Both parts are
 * removed. This file now drives the real component and the real dictionaries and
 * keeps only assertions that are true of the shipped UI, described as a placeholder.
 *
 * What exists on main (audited at 7322c561cc83222627aa3512a59a3b4ca2ed747f):
 * - `HealthPlaceholderPage` is the only Health Care surface. `/personal/health` renders
 *   it as `entry="personal"` and `/create/health` as `entry="create"`.
 * - It is informational: no consent control, no document picker and no preview
 *   affordance exists in it, and no consent state exists in `apps/web` at all.
 *   There is therefore no private UI state that a revocation could fail to clear.
 *
 * Pinned here, against that real UI:
 * 1. Every control the placeholder renders is disabled and self-labels as unavailable,
 *    so no health-document action — the action revocation would have to guard —
 *    can be taken from it.
 * 2. The placeholder claims no consent surface: nothing it renders reads as a consent
 *    grant, revocation or toggle.
 * 3. Its rendered privacy note denies collecting, uploading, storing or analyzing
 *    health information, which is the current truth revocation must not contradict.
 * 4. In every shipped locale the planned actions stay labelled unavailable over an
 *    unchanged feature set, so no locale ships a half-enabled revocable surface.
 * 5. The shipped product promise that a future health feature requires explicit
 *    consent and clear workspace ownership controls is still present in the real
 *    translation dictionary — the requirement a future revocation flow must satisfy.
 *
 * Intentional future failure: assertion 2 fails the moment a consent control is added
 * to this surface, at which point the revocation transition must be tested through
 * that component or shared state rather than through a model living in this file.
 *
 * Test-only, one path, synthetic-free: edits no source file, claims no owner-held
 * path, and makes no API, provider, model, storage or network call.
 */

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
