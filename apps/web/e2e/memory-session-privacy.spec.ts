import { test, expect } from "@playwright/test";

const workspaceA = "11111111-1111-4111-8111-111111111111";
const workspaceB = "22222222-2222-4222-8222-222222222222";

function syntheticSession(userId: string, workspaceId: string, email: string) {
  return {
    user: {
      id: userId,
      email,
      displayName: email,
      preferredLanguage: "en",
      personalWorkspaceId: workspaceId,
      createdAt: "2026-01-01T00:00:00Z",
      defaultGenerationLanguage: "en",
      timeZone: "UTC",
      outputPreference: "balanced",
      includeSourceLinks: true,
      onboardingCompletedAt: "2026-01-01T00:00:00Z",
      onboardingIntent: "project",
      isAdmin: false,
    },
    personalWorkspace: {
      id: workspaceId,
      name: "Synthetic personal workspace",
      slug: `synthetic-${userId}`,
      type: "Personal",
      role: "Owner",
    },
  };
}

const sessionA = syntheticSession("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", workspaceA, "memory-a@example.test");
const sessionB = syntheticSession("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", workspaceB, "memory-b@example.test");

function syntheticMemory(workspaceId: string, id: string, content: string) {
  return [{
    id,
    workspaceId,
    category: "Personal",
    title: "Synthetic private note",
    content,
    source: "Manual",
    isActive: true,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
  }];
}

test("revalidates private memories after a cross-tab account switch", async ({ browser }) => {
  const context = await browser.newContext();
  let currentSession = sessionA;

  await context.route("**/api/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === "/api/auth/me") {
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(currentSession) });
      return;
    }
    if (url.pathname === "/api/notifications/unread-count") {
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ unreadCount: 0 }) });
      return;
    }
    const memoryMatch = url.pathname.match(/^\/api\/workspaces\/([^/]+)\/memories$/);
    if (memoryMatch) {
      const session = currentSession;
      const rows = session.user.id === sessionA.user.id
        ? syntheticMemory(workspaceA, "memory-a", "SYNTHETIC_USER_A_PRIVATE_MEMORY")
        : syntheticMemory(workspaceB, "memory-b", "SYNTHETIC_USER_B_PRIVATE_MEMORY");
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(rows) });
      return;
    }
    await route.fulfill({ status: 404, contentType: "application/json", body: JSON.stringify({ error: "Synthetic E2E route not configured" }) });
  });

  try {
    const page = await context.newPage();
    await page.goto("/account/memory");
    await expect(page.getByText("SYNTHETIC_USER_A_PRIVATE_MEMORY", { exact: true })).toBeVisible();

    // A second same-origin tab can replace the shared cookie-backed session
    // without changing this tab's in-memory AuthProvider state. Model that
    // server-side identity change, then return this tab to the foreground.
    currentSession = sessionB;
    await page.evaluate(() => document.dispatchEvent(new Event("visibilitychange")));

    await expect(page.getByText("SYNTHETIC_USER_A_PRIVATE_MEMORY", { exact: true })).toHaveCount(0);
    await expect(page.getByText("SYNTHETIC_USER_B_PRIVATE_MEMORY", { exact: true })).toBeVisible();
  } finally {
    await context.close();
  }
});
