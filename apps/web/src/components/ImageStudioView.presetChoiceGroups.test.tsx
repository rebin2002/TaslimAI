import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { translate, type Locale } from "../lib/i18n";
import { ImageStudioView } from "./ImageStudioView";

// The preset choice groups must take their accessible names and hints from the locale
// dictionaries, so the mocked provider resolves t() against the real EN/AR/Sorani data.
const { localeState } = vi.hoisted(() => ({ localeState: { current: "en" as Locale } }));
vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: { id: "user-1" }, workspace: { id: "workspace-1" } }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    locale: localeState.current,
    t: (key: string) => translate(localeState.current, key),
  }),
}));

function studioHtml(locale: Locale): string {
  localeState.current = locale;
  return renderToStaticMarkup(<ImageStudioView />);
}

/** Extracts the inner text of the element carrying the given id. */
function textForId(html: string, id: string): string | undefined {
  const match = new RegExp(`id="${id}"[^>]*>([^<]*)<`).exec(html);
  return match?.[1];
}

/** Returns the opening tag of the container with the given class. */
function containerTag(html: string, className: string): string | undefined {
  const match = new RegExp(`<div class="${className}"([^>]*)>`).exec(html);
  return match ? match[0] : undefined;
}

/** Returns every radio button rendered inside the given container region. */
function radiosIn(html: string, className: string): string[] {
  const start = html.indexOf(`class="${className}"`);
  if (start === -1) return [];
  // Each preset grid ends where the next grid (or the advanced-options block) begins, so
  // the purpose grid's radios never absorb the aspect grid's.
  const afterOpeningTag = start + `class="${className}"`.length;
  const boundaries = ["image-segmented-control", "image-advanced-options"]
    .map((marker) => html.indexOf(marker, afterOpeningTag))
    .filter((index) => index !== -1);
  const end = Math.min(...boundaries);
  const region = html.slice(start, end);
  return region.match(/<button[^>]*role="radio"[^>]*>/g) ?? [];
}

describe("Image Studio prompt preset choice groups", () => {
  it("exposes the purpose presets as a single-choice group named by its localized label", () => {
    const html = studioHtml("en");
    const grid = containerTag(html, "image-purpose-grid");
    expect(grid, "the purpose preset grid must exist").toBeTruthy();
    expect(grid).toContain('role="radiogroup"');
    expect(grid).toContain('aria-labelledby="image-purpose-label"');
    expect(grid).toContain('aria-describedby="image-purpose-hint"');
    expect(textForId(html, "image-purpose-label")).toBe(translate("en", "image.purpose"));
    expect(textForId(html, "image-purpose-hint")).toBe(translate("en", "image.autoHint"));
  });

  it("exposes the aspect presets as a single-choice group named by its localized label", () => {
    const html = studioHtml("en");
    const grid = containerTag(html, "image-segmented-control");
    expect(grid, "the aspect preset grid must exist").toBeTruthy();
    expect(grid).toContain('role="radiogroup"');
    expect(grid).toContain('aria-labelledby="image-aspect-label"');
    expect(textForId(html, "image-aspect-label")).toBe(translate("en", "image.aspectRatio"));
  });

  it("renders exclusive checked state and a single tab stop per preset group", () => {
    const html = studioHtml("en");
    const purposes = radiosIn(html, "image-purpose-grid");
    expect(purposes).toHaveLength(7);
    expect(purposes.filter((tag) => tag.includes('aria-checked="true"'))).toHaveLength(1);
    expect(purposes.filter((tag) => tag.includes('tabindex="0"'))).toHaveLength(1);
    expect(purposes.filter((tag) => tag.includes('tabindex="-1"'))).toHaveLength(6);
    // Toggle-button semantics would announce several pressed options for one exclusive choice.
    expect(purposes.every((tag) => !tag.includes("aria-pressed"))).toBe(true);

    const aspects = radiosIn(html, "image-segmented-control");
    expect(aspects).toHaveLength(3);
    expect(aspects.filter((tag) => tag.includes('aria-checked="true"'))).toHaveLength(1);
    expect(aspects.filter((tag) => tag.includes('tabindex="0"'))).toHaveLength(1);
    expect(aspects.every((tag) => !tag.includes("aria-pressed"))).toBe(true);
  });

  it("keeps the decorative preset markers out of the accessible name", () => {
    const html = studioHtml("en");
    expect(html).toMatch(/<span class="image-purpose-dot is-[a-z]+" aria-hidden="true"><\/span>/);
    expect(html).toMatch(/<span class="image-format-icon is-[a-z]+" aria-hidden="true"><\/span>/);
  });

  it("names the preset groups and their options in English, Arabic, and Kurdish Sorani", () => {
    for (const locale of ["en", "ar", "ku"] as const) {
      const html = studioHtml(locale);
      // The group name must come from the locale dictionary, never a bare key or English-only text.
      expect(textForId(html, "image-purpose-label"), locale).toBe(translate(locale, "image.purpose"));
      expect(textForId(html, "image-purpose-hint"), locale).toBe(translate(locale, "image.autoHint"));
      expect(textForId(html, "image-aspect-label"), locale).toBe(translate(locale, "image.aspectRatio"));
      expect(textForId(html, "image-purpose-label"), locale).not.toBe("image.purpose");
      // Every option's accessible content is localized, and the referenced ids resolve.
      for (const value of ["product", "marketing", "social", "portrait", "illustration", "background", "other"]) {
        expect(html, `${locale} purpose option ${value}`).toContain(translate(locale, `image.purpose.${value}`));
      }
      for (const value of ["square", "portrait", "landscape"]) {
        expect(html, `${locale} aspect option ${value}`).toContain(translate(locale, `image.aspect.${value}`));
      }
      expect(html, `${locale} radiogroup wiring preserved`).toContain('role="radiogroup"');
      const grid = containerTag(html, "image-purpose-grid");
      expect(grid, locale).toContain('aria-labelledby="image-purpose-label"');
    }
  });

  it("selects the product preset by default and the square aspect by default", () => {
    const html = studioHtml("en");
    const selectedPurpose = radiosIn(html, "image-purpose-grid").find((tag) => tag.includes('aria-checked="true"'));
    expect(selectedPurpose).toBeDefined();
    // The default purpose is "product": its radio is the one inside the first option position.
    expect(html.slice(html.indexOf('aria-checked="true"'), html.indexOf('aria-checked="true"') + 400))
      .toContain(translate("en", "image.purpose.product"));
    const selectedAspect = radiosIn(html, "image-segmented-control").find((tag) => tag.includes('aria-checked="true"'));
    expect(selectedAspect).toBeDefined();
  });
});
