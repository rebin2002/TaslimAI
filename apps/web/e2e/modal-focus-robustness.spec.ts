import type { Page } from "@playwright/test";
import { expect, test } from "./fixtures";

// Independent-review browser regression for the shared dialog hook. The hook
// once enumerated Tab candidates with element-only hidden/aria-hidden checks,
// so CSS-hidden nodes, hidden/aria-hidden/inert ancestors and disabled
// fieldset members remained in its cycle list. A real browser refuses to move
// sequential focus into those nodes, which freezes the modal cycle at the
// previous control and breaks both wrap-around directions. This spec injects
// every reviewed element class into the live project dialog, derives the
// ground-truth sequential order from the browser itself, and pins that the
// hook's cycle matches it exactly: probes are never focused, the cycle
// advances through all real controls, wraps in both directions, and Escape
// still closes the dialog and restores the exact opener.
// All API traffic is intercepted: only synthetic GET responses are served,
// every write is recorded as a failure, and any unmapped GET path fails the
// test so silent endpoint drift cannot widen this spec's footprint.

const WORKSPACE_ID = "11111111-1111-4111-8111-111111111111";
const PROJECT_ID = "22222222-2222-4222-8222-222222222222";

const sessionPayload = {
  user: {
    id: "33333333-3333-4333-8333-333333333333",
    email: "modal-robustness-e2e@example.test",
    displayName: "Modal Robustness E2E",
    preferredLanguage: "en",
    personalWorkspaceId: WORKSPACE_ID,
    createdAt: "2026-11-01T00:00:00Z",
    defaultGenerationLanguage: "en",
    timeZone: "UTC",
    outputPreference: "balanced",
    includeSourceLinks: false,
    onboardingCompletedAt: "2026-11-01T00:00:00Z",
    onboardingIntent: "project",
    isAdmin: false,
  },
  personalWorkspace: {
    id: WORKSPACE_ID,
    name: "Modal Robustness Workspace",
    slug: "modal-robustness-workspace",
    type: "Personal",
    role: "Owner",
  },
};

const projectPayload = {
  id: PROJECT_ID,
  workspaceId: WORKSPACE_ID,
  name: "Robustness Focus Project",
  description: null,
  instructions: null,
  contextNotes: null,
  type: "Business",
  status: "Active",
  createdAt: "2026-11-01T00:00:00Z",
  updatedAt: "2026-11-01T00:00:00Z",
  archivedAt: null,
};

const PROBE_IDS = [
  "probe-visibility-hidden",
  "probe-display-none-input",
  "probe-fieldset-input",
  "probe-inert-button",
  "probe-aria-hidden-button",
  "probe-negative-tabindex-link",
] as const;

function json(body: unknown) {
  return { status: 200 as const, contentType: "application/json", body: JSON.stringify(body) };
}

async function installReadOnlyApiMock(page: Page) {
  const unmapped: string[] = [];
  const writes: string[] = [];
  await page.route(/\/api\//, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method().toUpperCase();
    if (method !== "GET" && method !== "HEAD") {
      writes.push(`${method} ${url.pathname}`);
      return route.fulfill({ status: 405, contentType: "application/json", body: "{}" });
    }
    const path = url.pathname;
    if (path.endsWith("/api/auth/me")) return route.fulfill(json(sessionPayload));
    if (path.includes("/projects")) return route.fulfill(json([projectPayload]));
    if (path === "/api/workspaces" || /^\/api\/workspaces\/[^/]+$/.test(path)) return route.fulfill(json([sessionPayload.personalWorkspace]));
    if (path.endsWith("/api/auth/csrf")) return route.fulfill(json({ token: "synthetic-csrf" }));
    if (path.endsWith("/api/notifications/unread-count") || path.endsWith("/api/activity/unread-count")) return route.fulfill(json({ unreadCount: 0 }));
    unmapped.push(`${method} ${path}${url.search}`);
    return route.fulfill({ status: 404, contentType: "application/json", body: "{}" });
  });
  return { unmapped, writes };
}
function writesOf(api: { writes: string[] }) {
  return api.writes;
}

async function injectUnfocusableProbes(page: Page) {
  await page.evaluate((probeIds) => {
    const dialog = document.querySelector<HTMLElement>(".project-form.modal-card");
    if (!dialog) throw new Error("project dialog is not mounted");
    // Append the probes after the real controls (end of the form), never
    // inside a wrapping <label>: visible probe text inside the name label
    // would join its accessible name and break label-based locators. The
    // trailing position reproduces the reviewed defect exactly: the old
    // element-only filter listed every probe after the last real control,
    // so native tabbing walked into the aria-hidden button (Chromium still
    // sequential-focuses aria-hidden nodes) and the backward boundary
    // focused the negative-tabindex link — nodes a correct cycle must never
    // visit — instead of wrapping close <-> create.
    const anchor =
      dialog.querySelector<HTMLElement>(".modal-actions") ?? dialog.lastElementChild ?? dialog;
    const invisible = document.createElement("button");
    invisible.type = "button";
    invisible.id = probeIds[0];
    invisible.textContent = "visibility hidden";
    invisible.style.visibility = "hidden";
    const displayWrapper = document.createElement("div");
    displayWrapper.style.display = "none";
    const displayInput = document.createElement("input");
    displayInput.id = probeIds[1];
    displayWrapper.append(displayInput);
    const fieldset = document.createElement("fieldset");
    fieldset.disabled = true;
    const fieldsetInput = document.createElement("input");
    fieldsetInput.id = probeIds[2];
    fieldset.append(fieldsetInput);
    const inertWrapper = document.createElement("div");
    inertWrapper.setAttribute("inert", "");
    const inertButton = document.createElement("button");
    inertButton.type = "button";
    inertButton.id = probeIds[3];
    inertWrapper.append(inertButton);
    const ariaWrapper = document.createElement("div");
    ariaWrapper.setAttribute("aria-hidden", "true");
    const ariaButton = document.createElement("button");
    ariaButton.type = "button";
    ariaButton.id = probeIds[4];
    ariaWrapper.append(ariaButton);
    const negativeLink = document.createElement("a");
    negativeLink.id = probeIds[5];
    negativeLink.href = "#probe";
    negativeLink.setAttribute("tabindex", "-2");
    negativeLink.textContent = "negatively tabbable";
    for (const node of [invisible, displayWrapper, fieldset, inertWrapper, ariaWrapper, negativeLink]) {
      anchor.after(node);
    }
  }, [...PROBE_IDS]);
}

const CANDIDATE_SELECTOR = "a[href],button,input,select,textarea,[tabindex]";

// Ground truth computed entirely inside the browser: a node belongs to the
// expected sequential cycle only when Chromium itself moves focus into it and
// it is not hidden from assistive technology (aria-hidden subtrees are
// deliberately excluded from dialog cycles by design, even though Chromium
// still permits programmatic focus there).
async function expectedSequentialOrder(page: Page): Promise<string[]> {
  return page.evaluate((selector) => {
    const dialog = document.querySelector<HTMLElement>(".project-form.modal-card");
    if (!dialog) throw new Error("project dialog is not mounted");
    const nodes = Array.from(dialog.querySelectorAll<HTMLElement>(selector));
    const previous = document.activeElement as HTMLElement | null;
    const order: string[] = [];
    nodes.forEach((node, index) => {
      const tabindex = node.getAttribute("tabindex");
      if (tabindex !== null && Number.parseInt(tabindex, 10) < 0) return;
      if (node.closest('[aria-hidden="true"]')) return;
      if (node.closest("[inert]")) return;
      const own = window.getComputedStyle(node);
      if (own.visibility === "hidden") return;
      if (node instanceof HTMLInputElement && node.type === "hidden") return;
      for (let walk: HTMLElement | null = node; walk && walk !== dialog; walk = walk.parentElement) {
        if (window.getComputedStyle(walk).display === "none") return;
      }
      try {
        node.focus();
      } catch {
        // focus() only throws for detached nodes; the loop stays conservative.
      }
      if (document.activeElement === node) order.push(`${node.tagName.toLowerCase()}@${index}`);
    });
    previous?.focus();
    return order;
  }, CANDIDATE_SELECTOR);
}

async function activeElementDescriptor(page: Page): Promise<string | null> {
  return page.evaluate((selector) => {
    const active = document.activeElement;
    if (!(active instanceof HTMLElement)) return null;
    const dialog = document.querySelector<HTMLElement>(".project-form.modal-card");
    if (!dialog || !dialog.contains(active)) return "outside-dialog";
    const nodes = Array.from(dialog.querySelectorAll<HTMLElement>(selector));
    return `${active.tagName.toLowerCase()}@${nodes.indexOf(active)}`;
  }, CANDIDATE_SELECTOR);
}

test("project dialog keyboard cycle skips every non-focusable node the review flagged", async ({ authenticatedPage: page }) => {
  // /projects is server-gated on main, so authenticate through the shared
  // fixture first and layer the read-only synthetic mock on top, exactly like
  // modal-keyboard-focus.spec.ts.
  const api = await installReadOnlyApiMock(page);
  await page.goto("/projects");
  const newProjectButton = page.getByRole("button", { name: /^new project$/i }).first();
  await expect(newProjectButton).toBeVisible();
  await newProjectButton.focus();
  await page.keyboard.press("Enter");
  const createDialog = page.getByRole("dialog", { name: /^create a project$/i });
  await expect(createDialog).toBeVisible();
  const nameField = createDialog.getByLabel(/^project name$/i);
  await expect(nameField).toBeFocused();

  await injectUnfocusableProbes(page);
  const expected = await expectedSequentialOrder(page);
  expect(expected.length, "the create dialog must expose a real keyboard cycle").toBeGreaterThanOrEqual(4);

  await nameField.focus();
  // The ground-truth list runs in DOM order from the dialog's first control
  // (its close button), while the trap starts at the autofocused name field;
  // rotate the lap assertions onto that start.
  const startDesc = await activeElementDescriptor(page);
  expect(startDesc, "the focused autofield must report a control inside the dialog").not.toBeNull();
  const start = expected.indexOf(startDesc ?? "");
  expect(start, "the autofocused field must appear in the ground-truth cycle").toBeGreaterThanOrEqual(0);
  const seen = new Set<string>([startDesc ?? ""]);
  for (let press = 1; press <= expected.length; press += 1) {
    await page.keyboard.press("Tab");
    const active = await activeElementDescriptor(page);
    expect(active, `Tab #${press} must follow the browser ground truth`).toBe(
      expected[(start + press) % expected.length],
    );
    if (active) seen.add(active);
  }
  expect(seen.size, "every real control must receive focus exactly once per lap").toBe(expected.length);
  // The full lap passes the forward boundary (after the last real control the
  // trailing probes sit; the old element-only filter let native navigation
  // fall onto its aria-hidden probe and the focusin rescue then skipped the
  // real wrap) and returns to the start field.
  await expect(nameField).toBeFocused();
  // Reverse direction wraps to the last REAL control, not to a probe: with
  // the old filter, Shift+Tab from the first control called focus() on the
  // trailing unfocusable probe and froze the cycle.
  await page.keyboard.press("Shift+Tab");
  await expect(activeElementDescriptor(page)).resolves.toBe(
    expected[(start - 1 + expected.length) % expected.length],
  );
  await page.keyboard.press("Shift+Tab");
  await expect(activeElementDescriptor(page)).resolves.toBe(
    expected[(start - 2 + expected.length) % expected.length],
  );
  for (let press = 1; press <= 3; press += 1) {
    await page.keyboard.press("Shift+Tab");
    const active = await activeElementDescriptor(page);
    expect(active).not.toBeNull();
    expect(active).not.toBe("outside-dialog");
  }

  await page.keyboard.press("Escape");
  await expect(createDialog).toBeHidden();
  await expect(newProjectButton).toBeFocused();
  expect(writesOf(api), "the modal flow must not reach any API write endpoint").toEqual([]);
  expect(api.unmapped, "unexpected API reads indicate endpoint drift").toEqual([]);
});
