import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { LocaleProvider } from "@/components/LocaleProvider";
import MusicStudioError from "./error";

/**
 * Synthetic fixtures only: a fabricated error value and a stub reset action.
 * No provider, network, or user content is involved.
 */
const SYNTHETIC_MESSAGE = "synthetic private provider response";
const SYNTHETIC_DIGEST = "synthetic-digest";

function renderBoundary() {
  return renderToStaticMarkup(
    <LocaleProvider>
      <MusicStudioError
        error={Object.assign(new Error(SYNTHETIC_MESSAGE), { digest: SYNTHETIC_DIGEST })}
        reset={vi.fn()}
      />
    </LocaleProvider>,
  );
}

/** Return the open tag of the element that carries the alert live region. */
function alertOpenTag(html: string): string {
  const match = html.match(/<[a-z]+[^>]*role="alert"[^>]*>/i);
  expect(match, "the recovery boundary must expose an alert live region").not.toBeNull();
  return match![0];
}

describe("Music Studio route recovery focus restoration", () => {
  it("renders a focusable alert panel with an accessible name and description", () => {
    const html = renderBoundary();
    const panel = alertOpenTag(html);

    // When the boundary takes over, React unmounts the crashed subtree along
    // with the element that held focus, so focus silently drops to
    // document.body. Without a tabindex the recovery panel is not focusable
    // at all and the restore cannot land on it; without a name/description an
    // alert panel that receives focus is announced as an unnamed region.
    expect(panel).toContain('tabindex="-1"');
    expect(panel).toContain('role="alert"');
    // AppShell supplies the page's <main>; the nested recovery boundary must not add another.
    expect(html).not.toMatch(/<main\b/i);
    expect(panel).toContain('aria-live="assertive"');
    expect(panel).toContain('aria-labelledby="music-error-title"');
    expect(panel).toContain('aria-describedby="music-error-description"');
    expect(html).toContain('id="music-error-title"');
    expect(html).toContain('id="music-error-description"');
    expect(html).toContain('type="button"');
  });

  it("keeps private error details out of the recovery markup", () => {
    const html = renderBoundary();

    expect(html).not.toContain(SYNTHETIC_MESSAGE);
    expect(html).not.toContain(SYNTHETIC_DIGEST);
  });

  it("restores focus into the panel through the shared decision helper", () => {
    const source = readFileSync(new URL("./error.tsx", import.meta.url), "utf8");

    // The boundary must wire a panel reference into the shared
    // restoreRecoveryFocus helper (the same decision app/error.tsx makes) so
    // the panel receives focus when the crashed subtree dropped it.
    expect(source).toContain("useRef");
    expect(source).toContain("restoreRecoveryFocus(");
    // A retry that throws again can report the same digest, so the restore
    // must be keyed on the error identity, not on error.digest.
    expect(source).toMatch(/\}, \[error\]\);/);
    expect(source).not.toMatch(/}, \[error\.digest\]\);/);
    // Privacy invariants: the opaque digest may be logged, the payload may not.
    expect(source).not.toContain("error.message");
    expect(source).not.toContain("error.stack");
    expect(source).not.toContain("error.cause");
  });
});
