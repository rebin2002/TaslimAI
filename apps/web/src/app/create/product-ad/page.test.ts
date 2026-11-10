import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const routeSource = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");
const localeAdapterSource = readFileSync(
  new URL("../../../components/ProductAdInputWizardPage.tsx", import.meta.url),
  "utf8",
);

describe("product ad preview route", () => {
  it("uses its own static route and enforces server-side and client-side authentication", () => {
    expect(routeSource).toContain('export const dynamic = "force-dynamic";');
    expect(routeSource).toContain('await requireAuthenticatedPage("/create/product-ad");');
    expect(routeSource).toContain("<ProtectedPage><ProductAdInputWizardPage /></ProtectedPage>");
    expect(routeSource).not.toContain("router.push");
    expect(routeSource).not.toContain("router.replace");
  });

  it("passes the saved English, Arabic, or Sorani locale to the existing wizard", () => {
    expect(localeAdapterSource).toContain('import { useLocale } from "@/components/LocaleProvider";');
    expect(localeAdapterSource).toContain("<ProductAdInputWizard locale={locale} />");
    expect(localeAdapterSource).not.toContain("fetch(");
    expect(localeAdapterSource).not.toContain("/api/");
  });
});
