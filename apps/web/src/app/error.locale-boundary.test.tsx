/**
 * Issue #697 evidence: isolate the secondary `useLocale must be used within
 * LocaleProvider` failure class from malformed/unrecognized Server Action
 * request handling.
 *
 * The HTTP harness (`e2e/server-action-malformed-post.spec.ts`) proves the
 * request handler answers malformed action POSTs with 400/307 and never
 * renders an error boundary. These tests pin the complementary half: which
 * boundary is *capable* of throwing the locale-hook error if a future render
 * path ever invokes it outside `layout.tsx`'s provider tree, and which is not.
 *
 * Deterministic and anonymous: no network, no providers beyond LocaleProvider,
 * no credentials, opaque fixed fixture errors only.
 */
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { LocaleProvider } from "@/components/LocaleProvider";
import { localeDirection, localeTag, locales, translate, type Locale } from "@/lib/i18n";
import { readRootErrorLocale } from "@/lib/globalErrorI18n";
import AppRouteError from "./error";
import GlobalError from "./global-error";

/**
 * Stand-in for the opaque server-side failure logged next to production 500s
 * ("Server Reference ID did not match the expected format"). The boundary must
 * never surface this text regardless of how it is rendered.
 */
const anonymousActionError = () =>
  Object.assign(new Error('The Server Reference ID did not match the expected format. Received "0000000000000000000000000000000000000000".'), {
    digest: "fixture-digest-697",
  });

describe("route recovery boundary locale-context dependency (#697 isolation)", () => {
  it("app/error.tsx depends on LocaleProvider: outside the provider tree it throws the secondary locale error", () => {
    // This is the exact secondary error class observed adjacent to the
    // production 500s. It originates from error.tsx's `useLocale()` call when
    // Next renders the boundary without layout.tsx's LocaleProvider ancestor,
    // not from the request handler itself.
    expect(() =>
      renderToStaticMarkup(
        <AppRouteError error={anonymousActionError()} reset={vi.fn()} />,
      ),
    ).toThrow("useLocale must be used within LocaleProvider");
  });

  it("global-error.tsx root fallback renders without any provider and does not throw the locale error", () => {
    const html = renderToStaticMarkup(
      <GlobalError error={anonymousActionError()} reset={vi.fn()} />,
    );
    expect(html).toContain("Something went wrong");
    expect(html).not.toContain("useLocale must be used within LocaleProvider");
  });

  it("inside the provider tree the boundary renders safe recovery copy and never leaks the fixture error", () => {
    const html = renderToStaticMarkup(
      <LocaleProvider>
        <AppRouteError error={anonymousActionError()} reset={vi.fn()} />
      </LocaleProvider>,
    );
    expect(html).toContain("Something went wrong");
    expect(html).toContain('role="alert"');
    expect(html).not.toContain("Server Reference ID");
    expect(html).not.toContain("fixture-digest-697");
  });

  it("root fallback locale resolution covers English, Arabic, and Sorani without a provider", () => {
    // Fixed mapping from the issue's fallback-copy requirement, resolved via
    // the pure helper already used by global-error.tsx.
    const cases: ReadonlyArray<{ documentLanguage: string; expected: Locale; tag: string; dir: string }> = [
      { documentLanguage: "en", expected: "en", tag: "en", dir: "ltr" },
      { documentLanguage: "ar", expected: "ar", tag: "ar", dir: "rtl" },
      { documentLanguage: "ckb", expected: "ku", tag: "ckb", dir: "rtl" },
    ];
    for (const { documentLanguage, expected, tag, dir } of cases) {
      const locale = readRootErrorLocale(documentLanguage, null);
      expect(locale).toBe(expected);
      expect(localeTag(locale)).toBe(tag);
      expect(localeDirection(locale)).toBe(dir);
    }
    for (const locale of locales) {
      const title = translate(locale, "error.title");
      expect(title).not.toBe("error.title");
      expect(title.length).toBeGreaterThan(0);
    }
  });
});
