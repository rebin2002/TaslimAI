import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { HealthPlaceholderPage } from "./HealthPlaceholderPage";
import { LocaleProvider } from "./LocaleProvider";
import { healthCareCopy } from "@/lib/healthCareI18n";
import { locales } from "@/lib/i18n";

describe("Health Care privacy boundary", () => {
  it("renders a truthful coming-soon page with all planned actions disabled", () => {
    const html = renderToStaticMarkup(<LocaleProvider><HealthPlaceholderPage /></LocaleProvider>);

    expect(html).toContain('<h1 id="health-care-title">Health Care</h1>');
    expect(html).toContain('role="status"');
    expect(html).toContain("Coming soon");
    expect(html).toContain("does not collect, upload, store, or analyze health information");
    expect(html).toContain("not medical advice, diagnosis, treatment, or emergency care");
    expect(html).toContain('aria-describedby="health-coming-soon health-privacy-note health-disclaimer"');
    expect(html).toContain('dir="ltr" lang="en"');
    expect(html).not.toContain("Health Studio");
    expect(html).not.toContain("<form");
    expect(html).not.toContain("<input");
    expect(html).not.toContain("<textarea");
    expect(html).not.toContain("/api/");
    expect(html).not.toContain("openai");

    const disabledButtons = html.match(/<button\b[^>]*\bdisabled=""/g) ?? [];
    expect(disabledButtons).toHaveLength(4);
    expect(html).toContain('href="/create"');
  });

  it("keeps complete Health Care copy available for every supported locale", () => {
    for (const locale of locales) {
      const copy = healthCareCopy(locale);
      expect(copy.title.trim().length, `title is present for ${locale}`).toBeGreaterThan(0);
      expect(copy.privacyNote.trim().length, `privacy note is present for ${locale}`).toBeGreaterThan(0);
      expect(copy.safetyNote.trim().length, `safety note is present for ${locale}`).toBeGreaterThan(0);
      expect(copy.features).toHaveLength(4);
      expect(copy.features.every((feature) => feature.label.trim().length > 0)).toBe(true);
    }
  });

  it("keeps both Health routes behind an independent server-side session gate", () => {
    const personalRoute = readFileSync(new URL("../app/personal/health/page.tsx", import.meta.url), "utf8");
    const createRoute = readFileSync(new URL("../app/create/health/page.tsx", import.meta.url), "utf8");

    expect(personalRoute).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(personalRoute).toContain('await requireAuthenticatedPage("/personal/health");');
    expect(createRoute).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(createRoute).toContain('await requireAuthenticatedPage("/create/health");');
    expect(createRoute).toContain('export const dynamic = "force-dynamic";');
    expect(createRoute).toContain("export const metadata: Metadata");
    expect(createRoute).toContain("index: false");
    expect(createRoute).toContain("follow: false");
    expect(createRoute).toContain("return <ProtectedPage>");
  });

  it("keeps route failures generic and excludes exception details", () => {
    const routeError = readFileSync(new URL("../app/create/health/error.tsx", import.meta.url), "utf8");

    expect(routeError).toContain('role="alert"');
    expect(routeError).toContain('aria-live="assertive"');
    expect(routeError).toContain("error.digest");
    expect(routeError).not.toContain("error.message");
    expect(routeError).not.toContain("error.stack");
    expect(routeError).not.toContain("error.cause");
  });
});
