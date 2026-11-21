import type { Page } from "@playwright/test";
import { test, expect, apiJson, registerInUi, logoutInUi, E2E_PASSWORD, type TestUser } from "./fixtures";

// Reproduction: a production `next start` serves the statically prerendered
// authenticated workspace shells with `Cache-Control: s-maxage=31536000` and
// no session awareness, which invites CDNs and corporate forward proxies to
// retain one year of cookie-authenticated route documents keyed only by URL.
// The isolation middleware must replace that with the private no-store
// policy on every non-public document, while leaving the credential forms
// alone. See apps/web/src/middleware.ts.

const ALPHA_MARKER = `ALPHA-SESSION-PRIVATE-${Date.now()}`;
const BETA_MARKER = `BETA-SESSION-PRIVATE-${Date.now()}`;

function makeUser(marker: string): TestUser {
  return {
    displayName: `E2E ${marker}`,
    email: `e2e+${marker.toLowerCase()}-${Math.random().toString(36).slice(2, 8)}@example.test`,
    password: E2E_PASSWORD,
  };
}

async function personalWorkspaceId(page: Page): Promise<string> {
  const auth = await apiJson<{ personalWorkspace: { id: string } }>(page.request, "GET", "/api/auth/me");
  return auth.personalWorkspace.id;
}

test.describe("authenticated SSR route cache isolation", () => {
  test("workspace home document is never shared-cacheable", async ({ page }) => {
    const response = await page.goto("/chat");
    expect(response).not.toBeNull();
    const headers = response!.headers();
    // Fail closed: no shared-cache lifetime may survive on a private route,
    // and the document must be marked private and unindexable.
    // `next dev` collapses every document to bare `no-store`, so the portable
    // contract is: never shared-cacheable, always no-store, always noindex.
    // A production `next start` answers with the full private directive list.
    expect(headers["cache-control"] ?? "").not.toMatch(/s-maxage|max-age=(?!0[,\s]|0$)/);
    expect(headers["cache-control"] ?? "").toMatch(/no-store/);
    expect(headers["x-robots-tag"] ?? "").toMatch(/noindex/);
  });

  test("route documents for other private surfaces carry the same policy", async ({ page }) => {
    for (const path of ["/", "/projects", "/activity"]) {
      const response = await page.goto(path);
      expect(response, path).not.toBeNull();
      expect(response!.headers()["x-robots-tag"] ?? "", path).toMatch(/noindex/);
    }
  });

  test("public credential forms stay outside the private policy", async ({ page }) => {
    const response = await page.goto("/register");
    expect(response).not.toBeNull();
    expect(response!.headers()["x-robots-tag"] ?? "").toBe("");
  });

  // The App Router also answers `<route>.rsc` with that route's serialized
  // payload (`Content-Type: text/x-component`). Those URLs are keyed by a
  // private route's path, so they belong inside the guarded set; before the
  // matcher gained its second entry a single top-level segment such as
  // `/search.rsc` satisfied the static-file carve-out instead, while the deeper
  // `/create/image.rsc` was already guarded. The payload of an allowlisted
  // credential form stays untouched, because Next resolves it back to that
  // form page before the policy is consulted (verified on `next dev` and
  // `next start`).
  test("route payload URLs carry the private policy at every depth", async ({ page }) => {
    for (const path of ["/projects.rsc", "/chat.rsc", "/account/billing.rsc"]) {
      const response = await page.goto(path);
      expect(response, path).not.toBeNull();
      expect(response!.status(), path).toBeLessThan(300);
      const headers = response!.headers();
      expect(headers["cache-control"] ?? "", path).toMatch(/no-store/);
      expect(headers["cache-control"] ?? "", path).not.toMatch(/s-maxage|max-age=(?!0[,\s]|0$)/);
      expect(headers["x-robots-tag"] ?? "", path).toMatch(/noindex/);
    }
  });

  test("payload URL of a public credential form stays outside the private policy", async ({ page }) => {
    const response = await page.goto("/register.rsc");
    expect(response).not.toBeNull();
    expect(response!.headers()["x-robots-tag"] ?? "").toBe("");
  });

  test("a second session in the same browser never replays the first session's /chat document or data", async ({ page }) => {
    // Session A creates a private conversation marker.
    const alpha = makeUser(ALPHA_MARKER);
    await registerInUi(page, alpha);
    await apiJson(page.request, "POST", `/api/workspaces/${await personalWorkspaceId(page)}/conversations`, {
      title: ALPHA_MARKER,
    });
    const alphaChat = await page.goto("/chat");
    expect(alphaChat).not.toBeNull();
    await expect(page.getByText(ALPHA_MARKER)).toBeVisible();
    expect(alphaChat!.headers()["cache-control"] ?? "").toMatch(/no-store/);

    await logoutInUi(page);

    // Session B reuses the same browser (the cache an attacker would poison)
    // and must fetch a fresh document with zero traces of A's session.
    const beta = makeUser(BETA_MARKER);
    await registerInUi(page, beta);
    // Positive isolation control: prove the second session can load its own
    // private data. Without this, an empty/broken list would pass the test
    // merely by hiding Session A's marker.
    await apiJson(page.request, "POST", `/api/workspaces/${await personalWorkspaceId(page)}/conversations`, {
      title: BETA_MARKER,
    });
    const betaChat = await page.goto("/chat");
    expect(betaChat).not.toBeNull();
    expect(betaChat!.status()).toBe(200);
    expect(betaChat!.headers()["cache-control"] ?? "").toMatch(/no-store/);
    expect(betaChat!.headers()["cache-control"] ?? "").not.toMatch(/s-maxage/);
    await expect(page.getByText(BETA_MARKER)).toBeVisible();
    await expect(page.getByText(ALPHA_MARKER)).toHaveCount(0);
    await expect(page.getByText(alpha.displayName)).toHaveCount(0);
  });
});
