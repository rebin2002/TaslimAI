import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { localeDirection, localeTag, locales } from "@/lib/i18n";
import { ProductAdInputWizard } from "./ProductAdInputWizard";

/**
 * The wizard renders every visible string from its own `copyByLocale` dictionary,
 * with two exceptions, both in states the existing suites never mount in a
 * non-English locale:
 *
 *   1. the scope mask that the authenticated route SERVER-renders for an
 *      authenticated visitor, before the session draft has been read;
 *   2. the accessible name of the preview artwork, reached only once a preview
 *      exists (which static markup never does).
 *
 * #733 pins the Arabic/Sorani headings of the unmasked form, #757 the preview
 * status machine, #937 the draft-notice region, and the workspace isolation suite
 * the mask itself - only ever in English. So an Arabic or Sorani user's first
 * paint, the one delivered by the server, currently carries an English sentence
 * inside `lang="ar" dir="rtl"`, announced by assistive technology as
 * untranslated scaffolding.
 *
 * Deterministic and dependency-free: server rendering of the exact tree the route
 * commits, plus a source guard over the two literals. No provider call, no
 * network, no file input, no user data.
 */

const wizardSource = readFileSync(
  fileURLToPath(new URL("./ProductAdInputWizard.tsx", import.meta.url)),
  "utf8",
);

/** The authenticated route always renders the wizard with a principal (#736). */
const principal = { userId: "user-1", workspaceId: "workspace-1" };

/** English-only text that must never be what a non-English locale is served. */
const englishOnlyLeaks = [
  "Restoring saved product-ad draft",
  "Local product-photo preview",
];

/**
 * What an authenticated visitor's browser receives as first paint: the key
 * exists before hydration, so the masked commit is the tree on screen.
 */
function maskedCommit(locale: (typeof locales)[number]): string {
  return renderToStaticMarkup(<ProductAdInputWizard locale={locale} principal={principal} />);
}

describe("Product Ad wizard transient regions stay in the requested locale", () => {
  it("server-commits the scope mask in Arabic and Sorani, not in English", () => {
    for (const locale of ["ar", "ku"] as const) {
      const html = maskedCommit(locale);
      // The masked commit is real: this is the tree the browser receives first.
      expect(html).toContain(`lang="${localeTag(locale)}"`);
      expect(html).toContain(`dir="${localeDirection(locale)}"`);
      // It is not silent: a status region speaks while the draft is being read.
      expect(html).toMatch(/<p role="status">[^<>]+<\/p>/);
      // But it must not read as untranslated scaffolding.
      for (const leak of englishOnlyLeaks) {
        expect(html, `${locale} masked commit`).not.toContain(leak);
      }
    }
  });

  it("keeps the exact English mask text the isolation suite already asserts", () => {
    expect(maskedCommit("en")).toContain("Restoring saved product-ad draft");
  });

  it("names the preview artwork after the localized product-photo control", () => {
    // The sibling brand-logo img is already named from the dictionary, so the
    // artwork region is the only English accessible name in the preview panel.
    expect(wizardSource).toContain("alt={copy.brandLogo}");
    expect(wizardSource).not.toMatch(/aria-label="Local product-photo preview"/);
    expect(wizardSource).toMatch(/aria-label=\{copy\.previewArtworkLabel\}/);
  });

  it("keeps the dictionary as the only source of wizard text in every locale", () => {
    // No capitalized English literal left in the transient status region.
    expect(wizardSource).not.toMatch(/<p role="status">[A-Z]/);
    for (const locale of ["ar", "ku"] as const) {
      const start = wizardSource.indexOf(`  ${locale}: {`);
      const dictionary = wizardSource.slice(start, wizardSource.indexOf("};", start));
      expect(dictionary, `${locale} dictionary`).toContain("maskRestoring:");
      expect(dictionary, `${locale} dictionary`).toContain("previewArtworkLabel:");
    }
  });
});
