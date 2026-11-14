import { existsSync, readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { studioCategories } from "./navigation";
import { locales, translate } from "./i18n";

/**
 * Issue #714 truthfulness smoke for the mobile Create menu Health Care entry.
 *
 * Owner disposition (2026-11-02, issue #714): exactly one navigation-only
 * "Health Care" card, linking only to the authenticated coming-soon route
 * `/create/health` from PR #715, once the #671/#681 owner-path dispositions and
 * i18n claims are recorded. Until then the entry must be transparently absent.
 * Under no circumstance may the Create menu present an enabled Health Care card
 * that promises capabilities the linked screen does not actually provide.
 *
 * Test-only and independent: it edits no source file, touches no owner-claimed
 * path (#671 StudioChooser, #681 create page, #715 health route, i18n.ts),
 * and validates both the live navigation data and synthetic future-state
 * fixtures. No personal health data or inference is used.
 */

type MenuEntry = { href: string; labelKey: string; descriptionKey: string };
type RouteScreenFact = { exists: boolean; source: string };

/** Routes the #714 disposition permits a health card to link to. */
const APPROVED_HEALTH_ROUTES = ["/create/health", "/personal/health"];

/** A health screen that collects data or calls provider surfaces is not privacy-safe. */
const COLLECTING_MARKERS = ["<input", "<textarea", "<form", "/api/"];

function pageUrlFor(href: string): URL {
  return new URL(`../app${href}/page.tsx`, import.meta.url);
}

function liveRouteFact(href: string): RouteScreenFact {
  const url = pageUrlFor(href);
  if (!existsSync(url)) return { exists: false, source: "" };
  return { exists: true, source: readFileSync(url, "utf8") };
}

function healthMenuEntries(entries: readonly MenuEntry[]): MenuEntry[] {
  return entries.filter((entry) => /health/i.test(`${entry.href} ${entry.labelKey} ${entry.descriptionKey}`));
}

/**
 * Deterministic truthfulness guard shared by the live assertions and the
 * synthetic fixtures. An enabled health card is acceptable only when every
 * fact below holds; any other state is a false enabled capability.
 */
function enabledCardTruthViolations(entry: MenuEntry, fact: RouteScreenFact): string[] {
  const violations: string[] = [];
  if (!APPROVED_HEALTH_ROUTES.includes(entry.href)) {
    violations.push(`${entry.href}: health card links outside the owner-approved privacy-safe routes`);
  }
  if (!fact.exists) {
    violations.push(`${entry.href}: enabled health card has no routable screen (false capability)`);
    return violations;
  }
  if (!fact.source.includes("requireAuthenticatedPage")) {
    violations.push(`${entry.href}: route screen is not server-gated for authentication`);
  }
  if (!fact.source.includes("ProtectedPage")) {
    violations.push(`${entry.href}: route screen is not client-gated for authentication`);
  }
  if (!fact.source.includes("index: false")) {
    violations.push(`${entry.href}: route screen does not opt out of indexing and broad publication`);
  }
  for (const marker of COLLECTING_MARKERS) {
    if (fact.source.includes(marker)) {
      violations.push(`${entry.href}: route screen exposes a health-data collection or provider surface (${marker})`);
    }
  }
  return violations;
}

const chooserEntries: MenuEntry[] = studioCategories.flatMap((category) =>
  category.studios.map((studio) => ({
    href: studio.href,
    labelKey: studio.labelKey,
    descriptionKey: studio.descriptionKey,
  })),
);

const healthEntries = healthMenuEntries(chooserEntries);

describe("issue #714 Health Care Create-menu truthfulness", () => {
  it("offers a Health Care card only when it links to a real privacy-safe screen", () => {
    const violations = healthEntries.flatMap((entry) => enabledCardTruthViolations(entry, liveRouteFact(entry.href)));
    expect(violations).toEqual([]);
    if (healthEntries.length === 0) {
      // Transparent-absence state: the menu exposes no enabled health card, and
      // the mobile chooser markup must not hardcode a hidden health link that
      // bypasses studioCategories until the #671 owner disposition is recorded.
      const chooserSource = readFileSync(new URL("../components/StudioChooser.tsx", import.meta.url), "utf8");
      expect(chooserSource).not.toContain("/create/health");
      expect(chooserSource.toLowerCase()).not.toContain("health");
    } else {
      // Enabled state: an exposed health card must be fully localized first
      // (English, Arabic, Sorani) per the #714 i18n hold.
      for (const entry of healthEntries) {
        for (const locale of locales) {
          expect(translate(locale, entry.labelKey), `${entry.labelKey} missing for ${locale}`).not.toBe(entry.labelKey);
          expect(translate(locale, entry.descriptionKey), `${entry.descriptionKey} missing for ${locale}`).not.toBe(entry.descriptionKey);
        }
      }
    }
  });

  it("routes every enabled Create-menu card to a real screen on disk", () => {
    for (const entry of chooserEntries) {
      expect(existsSync(pageUrlFor(entry.href)), `${entry.href} must resolve to a routable page`).toBe(true);
    }
  });

  describe("synthetic future-state fixtures", () => {
    const approvedCard: MenuEntry = {
      href: "/create/health",
      labelKey: "navigation.healthCare",
      descriptionKey: "create.studio.healthCareHint",
    };
    const privacySafeComingSoonScreen: RouteScreenFact = {
      exists: true,
      source: [
        'import { ProtectedPage } from "@/components/ProtectedPage";',
        'import { requireAuthenticatedPage } from "@/lib/serverAuth";',
        "  index: false,",
        '  await requireAuthenticatedPage("/create/health");',
        "  return <ProtectedPage><HealthPlaceholderPage /></ProtectedPage>;",
      ].join("\n"),
    };

    it("accepts the Health Care card wired to a real privacy-safe coming-soon screen", () => {
      expect(enabledCardTruthViolations(approvedCard, privacySafeComingSoonScreen)).toEqual([]);
    });

    it("treats a withdrawn entry as the transparently-disabled state", () => {
      const withoutHealthCard = chooserEntries.filter((entry) => !APPROVED_HEALTH_ROUTES.includes(entry.href));
      expect(healthMenuEntries(withoutHealthCard)).toEqual([]);
    });

    it("rejects an enabled card whose route has no screen", () => {
      const violations = enabledCardTruthViolations(approvedCard, { exists: false, source: "" });
      expect(violations.join("\n")).toMatch(/no routable screen \(false capability\)/);
    });

    it("rejects a card pointing at an unapproved health surface", () => {
      const diagnosisCard: MenuEntry = {
        href: "/create/symptom-doctor",
        labelKey: "navigation.healthCare",
        descriptionKey: "create.studio.healthCareHint",
      };
      const violations = enabledCardTruthViolations(diagnosisCard, {
        exists: true,
        source: "export default function Page() { return null; }",
      });
      expect(violations.join("\n")).toMatch(/owner-approved privacy-safe routes/);
    });

    it("rejects a route screen that collects data or calls provider surfaces", () => {
      const collectingScreen: RouteScreenFact = {
        exists: true,
        source: `${privacySafeComingSoonScreen.source}\n<form><input name="lab-report" /><button onClick={() => fetch("/api/health/analyze")}>Analyze</button></form>`,
      };
      const violations = enabledCardTruthViolations(approvedCard, collectingScreen);
      expect(violations.join("\n")).toMatch(/<input/);
      expect(violations.join("\n")).toMatch(/provider surface \(\/api\/\)/);
    });

    it("rejects a route screen that is not authenticated or that allows publication", () => {
      const ungatedScreen: RouteScreenFact = {
        exists: true,
        source: "export default function Page() { return <HealthPlaceholderPage />; }",
      };
      const violations = enabledCardTruthViolations(approvedCard, ungatedScreen);
      expect(violations.join("\n")).toMatch(/server-gated/);
      expect(violations.join("\n")).toMatch(/client-gated/);
      expect(violations.join("\n")).toMatch(/indexing/);
    });
  });
});
