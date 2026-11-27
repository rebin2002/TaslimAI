/**
 * Deterministic regression for the browser-side wiring of
 * `readBrowserRootErrorLocale()` — the only untested export on the #697
 * provider-free recovery path.
 *
 * Why this file exists and what it deliberately does NOT duplicate:
 * - `globalErrorI18n.test.ts` pins the pure resolver `readRootErrorLocale`
 *   (explicit document language + explicit storage passed in as arguments).
 * - `not-found.test.tsx` (#613 source lane / #1153 re-cut) pins the provider-free
 *   static shell and uses the pure resolver for its EN/AR/KU table.
 * - Neither suite ever exercises `readBrowserRootErrorLocale()` itself, which
 *   `app/not-found.tsx` calls inside its post-hydration effect (the locale
 *   restore that makes a provider-free 404 readable in Arabic/Sorani) and which
 *   `app/global-error.tsx` calls during render. The axes below are the actual
 *   browser reads: `document.documentElement.lang`, `window.localStorage` under
 *   the anonymous key, exception tolerance in privacy-restricted contexts, and
 *   the no-browser (server/first-paint) fail-closed path.
 *
 * Stacked evidence-only lane on #1153 head `0c7b8fb237716b669975a51bf89347a31e6c679e`.
 * Test-only: no source file touched. Deterministic and anonymous: fixed synthetic
 * constants, `vi.stubGlobal` only, no network, no provider calls, no credentials,
 * no production probes.
 */
import { afterEach, describe, expect, it, vi } from "vitest";
import { anonymousLocaleStorageKey } from "./localePersistence";
import { readBrowserRootErrorLocale } from "./globalErrorI18n";

const storageReturning = (value: string | null, seen: string[] = []) => ({
  getItem: (key: string) => {
    seen.push(key);
    return key === anonymousLocaleStorageKey ? value : null;
  },
  seen,
});

const stubBrowser = (
  documentLanguage: string | null | undefined,
  storage: { getItem: (key: string) => string | null } | null | undefined,
) => {
  vi.stubGlobal("document", { documentElement: { lang: documentLanguage } });
  if (storage) vi.stubGlobal("window", { localStorage: storage });
};

afterEach(() => vi.unstubAllGlobals());

describe("browser root-recovery locale wiring (provider-free path)", () => {
  it("fails closed to English when no browser context exists", () => {
    // The server/first-paint read inside the boundary effect's environment:
    // neither `document` nor `window` may be touched, and the result must stay
    // the deterministic English shell the SSR markup already rendered.
    expect(typeof globalThis.document).toBe("undefined");
    expect(typeof globalThis.window).toBe("undefined");
    expect(() => readBrowserRootErrorLocale()).not.toThrow();
    expect(readBrowserRootErrorLocale()).toBe("en");
  });

  it("treats an already-visible non-English document locale as authoritative", () => {
    stubBrowser("ckb", null);
    expect(readBrowserRootErrorLocale()).toBe("ku");
    vi.unstubAllGlobals();

    stubBrowser("ar", null);
    expect(readBrowserRootErrorLocale()).toBe("ar");
    vi.unstubAllGlobals();

    // Same authority applies when the storage disagrees: document wins.
    stubBrowser("ar", storageReturning("ku"));
    expect(readBrowserRootErrorLocale()).toBe("ar");
    vi.unstubAllGlobals();
  });

  it("normalizes whitespace and case in the document language", () => {
    stubBrowser("  CKB ", null);
    expect(readBrowserRootErrorLocale()).toBe("ku");
    vi.unstubAllGlobals();
  });

  it("restores the persisted anonymous locale when the document is still English", () => {
    for (const stored of ["ar", "ku"] as const) {
      const storage = storageReturning(stored);
      stubBrowser("en", storage);
      expect(readBrowserRootErrorLocale()).toBe(stored);
      // Pins that recovery reads exactly the anonymous key, never an
      // account-scoped or guessed alternative.
      expect(storage.seen).toEqual([anonymousLocaleStorageKey]);
      vi.unstubAllGlobals();
    }
  });

  it("survives a throwing localStorage accessor in privacy-restricted contexts", () => {
    vi.stubGlobal("document", {
      documentElement: {
        get lang() {
          return "en";
        },
      },
    });
    vi.stubGlobal("window", {
      get localStorage() {
        throw new Error("synthetic SecurityError: storage blocked");
      },
    });
    expect(() => readBrowserRootErrorLocale()).not.toThrow();
    expect(readBrowserRootErrorLocale()).toBe("en");
  });

  it("ignores unsupported or absent stored values and unsupported document languages", () => {
    stubBrowser("en", storageReturning("de"));
    expect(readBrowserRootErrorLocale()).toBe("en");
    vi.unstubAllGlobals();

    stubBrowser("en", storageReturning(null));
    expect(readBrowserRootErrorLocale()).toBe("en");
    vi.unstubAllGlobals();

    stubBrowser("fr", null);
    expect(readBrowserRootErrorLocale()).toBe("en");
    vi.unstubAllGlobals();

    stubBrowser("", null);
    expect(readBrowserRootErrorLocale()).toBe("en");
    vi.unstubAllGlobals();

    stubBrowser(undefined, null);
    expect(readBrowserRootErrorLocale()).toBe("en");
  });
});
