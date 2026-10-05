import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { HealthPlaceholderPage } from "./HealthPlaceholderPage";
import { LocaleProvider } from "./LocaleProvider";
import { locales, translate } from "@/lib/i18n";

describe("Health placeholder safety boundary", () => {
  it("renders a non-interactive privacy and medical-safety notice", () => {
    const html = renderToStaticMarkup(<LocaleProvider><HealthPlaceholderPage /></LocaleProvider>);

    expect(html).toContain("Health tools are not available yet");
    expect(html).toContain("does not collect, store, or analyze health information");
    expect(html).toContain("not medical advice, diagnosis, or emergency care");
    expect(html).not.toContain("<input");
    expect(html).not.toContain("<textarea");
    expect(html).not.toContain("/api/");
  });

  it("provides every safety message in every supported locale", () => {
    const keys = [
      "health.eyebrow",
      "health.title",
      "health.description",
      "health.privacyLabel",
      "health.privacyNote",
      "health.disclaimerLabel",
      "health.disclaimer",
    ];

    for (const locale of locales) {
      for (const key of keys) {
        expect(translate(locale, key), `${key} is missing for ${locale}`).not.toBe(key);
      }
    }
  });

  it("keeps the health route behind server and client authentication gates", () => {
    const route = readFileSync(new URL("../app/personal/health/page.tsx", import.meta.url), "utf8");

    expect(route).toContain('import { ProtectedPage } from "@/components/ProtectedPage";');
    expect(route).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(route).toContain('import { HealthPlaceholderPage } from "@/components/HealthPlaceholderPage";');
    expect(route).toContain('await requireAuthenticatedPage("/personal/health");');
    expect(route).toContain('export const dynamic = "force-dynamic";');
    expect(route).toContain("return <ProtectedPage>");
  });
});
