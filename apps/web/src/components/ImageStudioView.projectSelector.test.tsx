import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { translate, type Locale } from "../lib/i18n";
import { ImageStudioView } from "./ImageStudioView";

/*
 * Regression for the Image Studio project selector's loading state.
 *
 * The selector's option list is fetched from the workspace, so the studio's first render — and
 * every render right after a workspace switch — happens while the list is still in flight. In that
 * window the selector must stay reachable and must say what it does not know yet: the browser's
 * `disabled` attribute removes a focused control from the accessibility tree and drops the caret,
 * and an affirmative "No project" claims an empty workspace the client has not observed.
 */

const { localeState } = vi.hoisted(() => ({ localeState: { current: "en" as Locale } }));
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams() }));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: { id: "user-1" }, workspace: { id: "workspace-1" } }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: localeState.current, t: (key: string) => translate(localeState.current, key) }),
}));

const studioSource = readFileSync(new URL("./ImageStudioView.tsx", import.meta.url), "utf8");

function studioHtml(locale: Locale = "en"): string {
  localeState.current = locale;
  return renderToStaticMarkup(<ImageStudioView />);
}

/** The `<select>` element the project label is attached to. */
function projectSelectTag(html: string): string {
  const start = html.indexOf('class="image-project-select"');
  expect(start, "the project selector must render").toBeGreaterThan(-1);
  const match = /<select[^>]*>/.exec(html.slice(start));
  expect(match, "the project selector must render a select").toBeTruthy();
  return match![0];
}

/** Every option markup inside the project selector, up to the label's closing tag. */
function projectOptions(html: string): string[] {
  const start = html.indexOf('class="image-project-select"');
  const region = html.slice(start, html.indexOf("</label>", start));
  return region.match(/<option[^>]*>[^<]*<\/option>/g) ?? [];
}

describe("Image Studio project selector loading state", () => {
  it("keeps the selector present in the accessibility tree while its options load", () => {
    const tag = projectSelectTag(studioHtml());
    // `disabled` drops the control from the tab order entirely, so the keyboard user never
    // reaches it and never learns that the studio can bind the image to a project.
    expect(tag).not.toMatch(/\sdisabled(?:=""|\s|>)/);
    expect(tag).not.toContain('tabindex="-1"');
    // The transient state belongs on the control itself so it is announced where the user is.
    expect(tag).toContain('aria-busy="true"');
    expect(tag).toContain('aria-disabled="true"');
  });

  it("says that the project list is loading instead of claiming the workspace has none", () => {
    const html = studioHtml();
    const options = projectOptions(html);
    expect(options, "no project may be offered before the list arrives").toHaveLength(1);
    expect(options[0]).toContain(translate("en", "image.projectLoading"));
    expect(options[0]).toMatch(/\sdisabled(?:=""|\s|>)/);
    expect(html).not.toContain(`>${translate("en", "image.noProject")}<`);
  });

  it("refuses to commit a project choice taken while the list is in flight", () => {
    // The control stays focusable, so its change handler is what keeps a mid-flight selection
    // from being written into the generation request (same contract as the retry/cancel guards).
    expect(studioSource).toMatch(/onChange=\{\(event\) => \{ if \(loadingProjects\) return; setProjectId\(event\.target\.value\); \}\}/);
    // The settled branch keeps the real option list, including the workspace-level choice.
    expect(studioSource).toContain('{loadingProjects ? <option value="" disabled>');
    expect(studioSource).toContain('<option value="">{t("image.noProject")}</option>');
    expect(studioSource).toContain("{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}");
  });

  it("names the loading placeholder in English, Arabic and Kurdish Sorani", () => {
    for (const locale of ["en", "ar", "ku"] as const) {
      const html = studioHtml(locale);
      const label = translate(locale, "image.projectLoading");
      expect(label, locale).not.toBe("image.projectLoading");
      expect(html, locale).toContain(`>${label}</option>`);
      expect(projectSelectTag(html), locale).toContain('aria-busy="true"');
    }
  });
});
