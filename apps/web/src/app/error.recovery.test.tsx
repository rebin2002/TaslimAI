import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { LocaleProvider } from "@/components/LocaleProvider";
import { restoreRecoveryFocus, shouldRestoreRecoveryFocus } from "@/lib/recoveryFocus";
import AppRouteError from "./error";

const SYNTHETIC_MESSAGE = "synthetic private provider response";
const SYNTHETIC_DIGEST = "synthetic-digest";

function renderBoundary() {
  return renderToStaticMarkup(
    <LocaleProvider>
      <AppRouteError
        error={Object.assign(new Error(SYNTHETIC_MESSAGE), { digest: SYNTHETIC_DIGEST })}
        reset={vi.fn()}
      />
    </LocaleProvider>,
  );
}

function panelStub(contains: boolean) {
  const focus = vi.fn();
  return { focus, panel: { contains: () => contains, focus } };
}

/** A stand-in for the element that held focus before the crashed subtree unmounted. */
function activeNode(id: string): Node {
  return { nodeType: 1, id } as unknown as Node;
}

describe("app recovery focus decision", () => {
  it("restores focus when the crashed subtree dropped it to the document body", () => {
    // A null active element is the `document.body` fallback left behind once
    // the focused control unmounts with the crashed tree.
    const dropped = panelStub(false);
    expect(restoreRecoveryFocus(dropped.panel, null)).toBe(true);
    expect(dropped.focus).toHaveBeenCalledTimes(1);

    const outside = panelStub(false);
    expect(shouldRestoreRecoveryFocus(outside.panel, activeNode("nav-link"))).toBe(true);
    expect(restoreRecoveryFocus(outside.panel, activeNode("nav-link"))).toBe(true);
    expect(outside.focus).toHaveBeenCalledTimes(1);
  });

  it("leaves focus alone when it is already inside the recovery panel", () => {
    const inside = panelStub(true);
    expect(shouldRestoreRecoveryFocus(inside.panel, activeNode("retry"))).toBe(false);
    expect(restoreRecoveryFocus(inside.panel, activeNode("retry"))).toBe(false);
    expect(inside.focus).not.toHaveBeenCalled();
  });

  it("does nothing when the panel reference is unavailable", () => {
    expect(shouldRestoreRecoveryFocus(null, null)).toBe(false);
    expect(restoreRecoveryFocus(undefined, null)).toBe(false);
  });
});

describe("app route recovery boundary", () => {
  it("renders a focusable alert panel with an accessible name and description", () => {
    const html = renderBoundary();

    expect(html).toContain('tabindex="-1"');
    expect(html).toContain('role="alert"');
    expect(html).toContain('aria-live="assertive"');
    expect(html).toContain('aria-labelledby="app-error-title"');
    expect(html).toContain('aria-describedby="app-error-description"');
    expect(html).toContain('type="button"');
  });

  it("keeps a single main landmark so mobile screen-reader navigation is not duplicated", () => {
    // AppShell already renders <main id="main-content"> and this boundary
    // renders inside it, so a second <main> yields two identical landmarks.
    const html = renderBoundary();

    expect(html).not.toContain("<main");
    expect(html).toContain('class="placeholder-page"');
  });

  it("keeps recovery copy localized and private details out of the markup", () => {
    const html = renderBoundary();

    expect(html).toContain("Something went wrong");
    expect(html).toContain("Try again");
    expect(html).toContain("Open Assets");
    expect(html).not.toContain(SYNTHETIC_MESSAGE);
    expect(html).not.toContain(SYNTHETIC_DIGEST);
  });

  it("restores focus on every failure, not only on a new digest", () => {
    const source = readFileSync(new URL("./error.tsx", import.meta.url), "utf8");

    expect(source).toContain("useRef");
    expect(source).toContain("restoreRecoveryFocus(");
    // A retry that throws again can report the same digest, so an effect keyed
    // on `error.digest` would skip the restore exactly when the user retries.
    expect(source).toMatch(/\}, \[error\]\);/);
    expect(source).not.toContain("error.message");
    expect(source).not.toContain("error.stack");
    expect(source).not.toContain("error.cause");
  });
});
