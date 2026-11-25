import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { healthCareCopy } from "@/lib/healthCareI18n";
import HealthCareRouteError from "./error";
/**
 * Synthetic fixtures only: a stub locale and a fabricated error value. No
 * provider, network, real translation catalog, account, navigation state, or
 * health information is involved, and nothing here reaches any backend.
 */
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    t: (key: string) => `synthetic:${key}`,
    locale: "en",
  }),
}));
const SYNTHETIC_MESSAGE = "SYNTHETIC_PRIVATE_HEALTH_ROUTE_ERROR";
const SYNTHETIC_DIGEST = "SYNTHETIC_HEALTH_ROUTE_DIGEST";
function renderBoundary() {
  return renderToStaticMarkup(
    <HealthCareRouteError
      error={Object.assign(new Error(SYNTHETIC_MESSAGE), { digest: SYNTHETIC_DIGEST })}
      reset={() => undefined}
    />,
  );
}
/** Return the open tag of the element that carries the alert live region. */
function alertOpenTag(html: string): string {
  const match = html.match(/<[a-z]+[^>]*role="alert"[^>]*>/i);
  expect(match, "the recovery boundary must expose an alert live region").not.toBeNull();
  return match![0];
}
describe("Health Care unavailable-view focus restoration", () => {
  it("makes the alert recovery panel focusable so assistive tech lands on it", () => {
    const panel = alertOpenTag(renderBoundary());
    // When the boundary takes over, React unmounts the crashed Health Care
    // preview together with the element that held focus, so focus silently
    // drops to document.body. Without a tabindex the panel is not focusable
    // at all and the shared restore cannot land on it. The Personal surface
    // (src/app/personal/health) and the Document and Music Studio boundaries
    // already satisfy this; the Create surface had to match.
    expect(panel).toContain('tabindex="-1"');
    expect(panel).toContain('role="alert"');
    expect(panel).toContain('aria-live="assertive"');
  });
  it("gives the focused panel an accessible name and description", () => {
    const html = renderBoundary();
    const panel = alertOpenTag(html);
    // A focused container that receives no name is announced as an unnamed
    // region, which leaves the screen-reader user without the recovery copy.
    expect(panel).toContain('aria-labelledby="health-care-error-title"');
    expect(panel).toContain('aria-describedby="health-care-error-description"');
    expect(html).toContain('id="health-care-error-title"');
    expect(html).toContain('id="health-care-error-description"');
  });
  it("keeps one main landmark, the localized copy, and the reading direction", () => {
    const html = renderBoundary();
    // AppShell supplies the page's <main>; the boundary must not add a second.
    expect(html).not.toMatch(/<main\b/i);
    const copy = healthCareCopy("en");
    expect(html).toContain(copy.errorTitle);
    expect(html).toContain(copy.errorDescription);
    expect(html).toContain(copy.retry);
    expect(html).toContain(copy.backToCreate);
    expect(html).toContain('type="button"');
    // The restore must not drop the bilingual/direction wrapper the preview
    // page owns: ar/ku render RTL, en renders LTR.
    expect(html).toContain('dir="ltr"');
    expect(html).toContain('lang="en"');
  });
  it("keeps private failure details out of the recovery markup", () => {
    const html = renderBoundary();
    expect(html).not.toContain(SYNTHETIC_MESSAGE);
    expect(html).not.toContain(SYNTHETIC_DIGEST);
  });
  it("restores focus into the panel through the shared decision helper", () => {
    const source = readFileSync(new URL("./error.tsx", import.meta.url), "utf8");
    // The boundary must wire its own panel reference into the shared
    // restoreRecoveryFocus decision, the same one app/error.tsx, the Music
    // Studio boundary, and the Document Studio boundary use.
    expect(source).toContain("useRef");
    expect(source).toContain("restoreRecoveryFocus(");
    // A retry that fails again can report the same digest, so the restore must
    // be keyed on the error identity rather than the digest value.
    expect(source).toMatch(/\}, \[error\]\);/);
    expect(source).not.toMatch(/restoreRecoveryFocus\([^)]*\);\s*\}, \[error\.digest\]\);/);
    expect(source).not.toContain("error.message");
    expect(source).not.toContain("error.stack");
    expect(source).not.toContain("error.cause");
  });
});
