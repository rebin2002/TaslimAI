import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { restoreRecoveryFocus, type RecoveryFocusPanel } from "@/lib/recoveryFocus";
import DocumentStudioError from "./error";

/**
 * Synthetic fixtures only: a stub translator and fabricated error values. No
 * provider, network, real translation catalog, document content, or user data
 * is involved, and nothing here reaches a rendering or export backend.
 */
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    t: (key: string) => `synthetic:${key}`,
  }),
}));

const SYNTHETIC_MESSAGE = "SYNTHETIC_PRIVATE_DOCUMENT_ERROR";
const SYNTHETIC_DIGEST = "SYNTHETIC_DOCUMENT_DIGEST";

function renderBoundary() {
  return renderToStaticMarkup(
    <DocumentStudioError
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

describe("Document Studio export recovery focus restoration", () => {
  it("makes the alert recovery panel focusable so assistive tech lands on it", () => {
    const panel = alertOpenTag(renderBoundary());

    // When the boundary takes over, React unmounts the crashed document
    // workspace together with the element that held focus, so focus silently
    // drops to document.body. Without a tabindex the panel is not focusable at
    // all and the restore cannot land on it.
    expect(panel).toContain('tabindex="-1"');
    expect(panel).toContain('role="alert"');
    expect(panel).toContain('aria-live="assertive"');
  });

  it("gives the focused panel an accessible name and description", () => {
    const html = renderBoundary();
    const panel = alertOpenTag(html);

    // A focused container that receives no name is announced as an unnamed
    // region, which leaves the screen-reader user without the recovery copy.
    expect(panel).toContain('aria-labelledby="document-error-title"');
    expect(panel).toContain('aria-describedby="document-error-description"');
    expect(html).toContain('id="document-error-title"');
    expect(html).toContain('id="document-error-description"');
  });

  it("keeps one main landmark and localized recovery copy", () => {
    const html = renderBoundary();

    // AppShell supplies the page's <main>; the boundary must not add a second.
    expect(html).not.toMatch(/<main\b/i);
    expect(html).toContain("synthetic:document.completedLoadError");
    expect(html).toContain("synthetic:document.completedLoadHint");
    expect(html).toContain("synthetic:document.retry");
    expect(html).toContain('type="button"');
  });

  it("keeps private failure details out of the recovery markup", () => {
    const html = renderBoundary();

    expect(html).not.toContain(SYNTHETIC_MESSAGE);
    expect(html).not.toContain(SYNTHETIC_DIGEST);
  });

  it("restores focus into the panel through the shared decision helper", () => {
    const source = readFileSync(new URL("./error.tsx", import.meta.url), "utf8");

    // The boundary must wire its own panel reference into the shared
    // restoreRecoveryFocus decision, the same one app/error.tsx and the Music
    // Studio boundary use.
    expect(source).toContain("useRef");
    expect(source).toContain("restoreRecoveryFocus(");
    // A retry that fails again can report the same digest, so the restore must
    // be keyed on the error identity rather than the digest value.
    expect(source).toMatch(/\}, \[error\]\);/);
    expect(source).not.toMatch(/}, \[error\.digest\]\);/);
    expect(source).not.toContain("error.message");
    expect(source).not.toContain("error.stack");
    expect(source).not.toContain("error.cause");
  });

  it("moves focus dropped to the body fallback, and leaves an owned panel alone", () => {
    // Behaviour contract the boundary relies on, exercised with fabricated DOM
    // nodes instead of a browser: the body fallback after an unmount must be
    // replaced by the panel, while a panel the user is already on keeps focus.
    const bodyFallback = {} as unknown as Node;
    const focusedPanelNode = {} as unknown as Node;
    const panel: RecoveryFocusPanel = { contains: (node: Node | null): boolean => node === focusedPanelNode, focus: vi.fn() };

    // Focus fell out of the unmounted subtree, so the panel has to take it.
    expect(restoreRecoveryFocus(panel, bodyFallback)).toBe(true);
    expect(panel.focus).toHaveBeenCalledTimes(1);
    // A re-render while the user is already on the panel must not steal focus.
    expect(restoreRecoveryFocus(panel, focusedPanelNode)).toBe(false);
    expect(panel.focus).toHaveBeenCalledTimes(1);
    // No panel reference means nothing to restore onto.
    expect(restoreRecoveryFocus(null, bodyFallback)).toBe(false);
  });
});
