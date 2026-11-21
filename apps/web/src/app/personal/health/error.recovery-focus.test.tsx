import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import HealthRouteError from "./error";

/**
 * Synthetic fixtures only: a stub translator and fabricated error values. No
 * provider, network, real translation catalog, or health data is involved.
 */
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    t: (key: string) => `synthetic:${key}`,
  }),
}));

const SYNTHETIC_MESSAGE = "SYNTHETIC_PRIVATE_HEALTH_ERROR";
const SYNTHETIC_DIGEST = "SYNTHETIC_HEALTH_DIGEST";

function renderBoundary() {
  return renderToStaticMarkup(
    <HealthRouteError
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

describe("Health route recovery focus restoration", () => {
  it("makes the alert recovery panel focusable so assistive tech lands on it", () => {
    const panel = alertOpenTag(renderBoundary());

    // The panel is the element focus must move to once the crashed subtree has
    // been removed. Without a tabindex it is not focusable and focus silently
    // falls back to document.body.
    expect(panel).toContain('tabindex="-1"');
    expect(panel).toContain('role="alert"');
    expect(panel).toContain('aria-live="assertive"');
    expect(panel).toContain('aria-labelledby="health-error-title"');
    expect(panel).toContain('aria-describedby="health-error-description"');
  });

  it("keeps recovery copy localized and private details out of the markup", () => {
    const html = renderBoundary();

    expect(html).toContain("synthetic:error.title");
    expect(html).toContain("synthetic:error.description");
    expect(html).toContain("synthetic:error.retry");
    expect(html).toContain('type="button"');
    expect(html).not.toContain(SYNTHETIC_MESSAGE);
    expect(html).not.toContain(SYNTHETIC_DIGEST);
  });

  it("restores focus on mount and on every repeated failure", () => {
    const source = readFileSync(new URL("./error.tsx", import.meta.url), "utf8");

    // Focus must be moved to the panel reference, and the effect must re-run for
    // a repeated failure (a retry that throws again) rather than only the first
    // digest value, which can stay identical across retries.
    expect(source).toContain("useRef");
    expect(source).toMatch(/const panel = panelRef\.current;/);
    expect(source).toMatch(/panel\.focus\(\);/);
    expect(source).toMatch(/\}, \[error\]\);/);
    expect(source).not.toMatch(/<main\b/);
    expect(source).toContain('<div className="placeholder-page">');
    expect(source).toContain("error.digest");
    expect(source).not.toContain("error.message");
    expect(source).not.toContain("error.stack");
    expect(source).not.toContain("error.cause");
  });
});
