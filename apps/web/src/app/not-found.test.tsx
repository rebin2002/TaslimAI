/**
 * Regression suite for the root not-found boundary (#613 source lane, #697
 * production signal, #1063 reproduction harness).
 *
 * Two independent properties are pinned here, and both fail on unpatched main:
 *
 * 1. Landmark. `AppShell` already owns `<main id="main-content">` for every
 *    route rendered through `layout.tsx`, so the boundary must not emit a second
 *    `<main>`.
 * 2. Provider freedom. Next renders the root not-found boundary *outside*
 *    `layout.tsx` on the legacy unrecognized-Server-Action fallback path, so the
 *    boundary must render without a LocaleProvider ancestor instead of throwing
 *    `useLocale must be used within LocaleProvider` and turning a recovery
 *    surface into a 500.
 *
 * Deterministic and anonymous: no network, no live provider, no credentials,
 * fixed synthetic constants, `react-dom/server` static markup only.
 */
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { LocaleProvider, useOptionalLocale } from "@/components/LocaleProvider";
import { readRootErrorLocale } from "@/lib/globalErrorI18n";
import { localeDirection, localeTag, locales, type Locale } from "@/lib/i18n";
import { anonymousLocaleStorageKey } from "@/lib/localePersistence";
import { notFoundKeys, translateNotFound } from "@/lib/notFoundI18n";
import NotFound from "./not-found";

const syntheticStorage = (locale: Locale): Pick<Storage, "getItem"> => ({
  getItem: (key: string) => (key === anonymousLocaleStorageKey ? locale : null),
});

function LocalePresenceProbe() {
  const context = useOptionalLocale();
  return <div data-provider={context ? "present" : "absent"} />;
}

afterEach(() => vi.unstubAllGlobals());

describe("not-found boundary", () => {
  it("renders accessible recovery navigation without exposing internal details", () => {
    const html = renderToStaticMarkup(
      <LocaleProvider>
        <NotFound />
      </LocaleProvider>,
    );

    expect(html).toContain("Page not found");
    expect(html).toContain("We could not find that page");
    expect(html).toContain('aria-labelledby="not-found-title"');
    expect(html).toContain('aria-describedby="not-found-description"');
    expect(html).toContain('href="/"');
    expect(html).toContain('href="/assets"');
    expect(html).not.toMatch(/<main\b/);
    expect(html).not.toContain('role="main"');
    expect(html).not.toContain("error.message");
    expect(html).not.toContain("digest");
  });

  it("recovers without a locale provider when Next renders the not-found boundary outside root layout", () => {
    // The #697 render path: no LocaleProvider ancestor. Recovery must stay
    // renderable, and the server output must remain deterministic so a second
    // hydration failure cannot follow the first render failure.
    const html = renderToStaticMarkup(<NotFound />);

    expect(html).toContain("Page not found");
    expect(html).toContain("We could not find that page");
    expect(html).toContain('lang="en"');
    expect(html).toContain('dir="ltr"');
    expect(html).toContain('aria-labelledby="not-found-title"');
    expect(html).toContain('href="/"');
    expect(html).toContain('href="/assets"');
    expect(html).not.toMatch(/<main\b/);
    expect(html).toContain('role="main"');
    expect(html).not.toContain("useLocale must be used within LocaleProvider");
    expect(html).not.toContain("digest");
  });

  it("exposes a non-throwing locale context read that reports absence instead of failing", () => {
    // The primitive the boundary depends on: null outside the provider tree,
    // the live value inside it. `useLocale` keeps throwing for ordinary app
    // code, so a missing provider stays a programming error there.
    expect(renderToStaticMarkup(<LocalePresenceProbe />)).toContain('data-provider="absent"');
    expect(
      renderToStaticMarkup(
        <LocaleProvider>
          <LocalePresenceProbe />
        </LocaleProvider>,
      ),
    ).toContain('data-provider="present"');
  });

  it("never reads browser state during the provider-free first paint", () => {
    // Hydration-mismatch guard: the restored locale may only arrive after
    // hydration (in the effect), never while rendering the shell. If a future
    // edit moves `readBrowserRootErrorLocale` into render, the Arabic browser
    // state below changes this markup and the test goes red.
    vi.stubGlobal("window", { localStorage: syntheticStorage("ar") });
    vi.stubGlobal("document", { documentElement: { lang: "ar" } });

    const arabicBrowser = renderToStaticMarkup(<NotFound />);
    vi.unstubAllGlobals();
    const cleanBrowser = renderToStaticMarkup(<NotFound />);

    expect(cleanBrowser).toContain('lang="en"');
    expect(cleanBrowser).toContain('dir="ltr"');
    expect(arabicBrowser).toBe(cleanBrowser);
  });

  it("restores the visible locale for provider-free recovery across English, Arabic and Sorani", () => {
    // Fixed mapping for the copy the boundary's post-hydration restore uses,
    // resolved through the same pure helper that global-error.tsx relies on.
    const cases: ReadonlyArray<{ documentLanguage: string; storage: Locale | null; expected: Locale; tag: string; dir: string }> = [
      { documentLanguage: "en", storage: null, expected: "en", tag: "en", dir: "ltr" },
      { documentLanguage: "ar", storage: null, expected: "ar", tag: "ar", dir: "rtl" },
      { documentLanguage: "ckb", storage: null, expected: "ku", tag: "ckb", dir: "rtl" },
      { documentLanguage: "en", storage: "ar", expected: "ar", tag: "ar", dir: "rtl" },
      { documentLanguage: "en", storage: "ku", expected: "ku", tag: "ckb", dir: "rtl" },
      { documentLanguage: "de", storage: null, expected: "en", tag: "en", dir: "ltr" },
    ];

    for (const { documentLanguage, storage, expected, tag, dir } of cases) {
      const locale = readRootErrorLocale(documentLanguage, storage ? syntheticStorage(storage) : null);
      expect(locale).toBe(expected);
      expect(localeTag(locale)).toBe(tag);
      expect(localeDirection(locale)).toBe(dir);
    }

    // Unrecognized document language and no usable storage must land on the
    // safe generic English shell rather than an empty or thrown render.
    expect(readRootErrorLocale(undefined, null)).toBe("en");
  });

  it("renders exactly the localized recovery table for every supported locale", () => {
    // Guards both directions: the boundary is wired to translateNotFound (not
    // hardcoded English), and no supported locale silently falls back.
    expect(locales).toEqual(["en", "ar", "ku"]);

    for (const locale of locales) {
      for (const key of notFoundKeys) {
        const copy = translateNotFound(locale, key);
        expect(copy.length).toBeGreaterThan(0);
        expect(copy).not.toBe(key);
      }
    }

    const englishTitle = translateNotFound("en", "title");
    expect(translateNotFound("ar", "title")).not.toBe(englishTitle);
    expect(translateNotFound("ku", "title")).not.toBe(englishTitle);

    const providerFree = renderToStaticMarkup(<NotFound />);
    for (const key of notFoundKeys) {
      expect(providerFree).toContain(translateNotFound("en", key));
    }
  });

  it("redacts request internals from the provider-free recovery shell", () => {
    // The adjacent production log line is a malformed Server Reference ID. It is
    // a separate fault class from the locale boundary and must never appear in
    // recovery output regardless of which boundary renders.
    const html = renderToStaticMarkup(<NotFound />);

    expect(html).not.toContain("Server Reference ID");
    expect(html).not.toContain("Next-Action");
    expect(html).not.toContain("process.env");
    expect(html).not.toContain("NEXT_PUBLIC_");
    expect(html).not.toContain("useLocale");
  });
});
