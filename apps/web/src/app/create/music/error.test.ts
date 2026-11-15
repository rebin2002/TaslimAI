import { createElement, isValidElement, type ReactNode } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { LocaleProvider } from "@/components/LocaleProvider";
import { locales, translate, type Locale } from "@/lib/i18n";
import MusicStudioError, { MusicStudioRecoveryView } from "./error";

function findRetryHandler(node: ReactNode): (() => void) | null {
  if (Array.isArray(node)) {
    for (const child of node) {
      const handler = findRetryHandler(child);
      if (handler) return handler;
    }
    return null;
  }
  if (!isValidElement<{ children?: ReactNode; onClick?: () => void }>(node)) return null;
  if (node.type === "button") return node.props.onClick ?? null;
  return findRetryHandler(node.props.children);
}

const localize = (locale: Locale) => (key: string) => translate(locale, key);

describe("Music Studio route recovery", () => {
  it.each(locales)("renders real, accessible localized recovery UI (%s)", (locale) => {
    const html = renderToStaticMarkup(createElement(MusicStudioRecoveryView, {
      reset: () => {},
      t: localize(locale),
    }));
    expect(html).toContain('role="alert"');
    expect(html).toContain('aria-live="assertive"');
    expect(html).toContain('type="button"');
    expect(html).toContain('href="/assets"');
    for (const key of ["music.title", "error.title", "error.description", "error.retry", "error.openAssets"]) {
      const translated = translate(locale, key);
      expect(translated, `${locale}/${key}`).not.toBe(key);
      expect(html, `${locale}/${key}`).toContain(translated);
    }
  });

  it("wires the visible retry button to exactly the supplied reset action", () => {
    const reset = vi.fn();
    const view = MusicStudioRecoveryView({ reset, t: localize("en") });
    const retry = findRetryHandler(view);
    expect(retry).toBeTypeOf("function");
    expect(reset).not.toHaveBeenCalled();
    retry?.();
    expect(reset).toHaveBeenCalledTimes(1);
  });

  it("never renders exception details in the actual route error wrapper", () => {
    const secret = "PRIVATE_PROVIDER_ERROR_DO_NOT_RENDER";
    const html = renderToStaticMarkup(createElement(LocaleProvider, null,
      createElement(MusicStudioError, { error: new Error(secret), reset: () => {} }),
    ));
    expect(html).toContain(translate("en", "error.title"));
    expect(html).toContain('href="/assets"');
    expect(html).not.toContain(secret);
    expect(html).not.toContain("stack");
    expect(html).not.toContain("digest");
  });
});
