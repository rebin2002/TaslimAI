import type { Page } from "@playwright/test";
import { apiJson, createProject, expect, test } from "./fixtures";

// Browser-side proof of the project revision fence. Two tabs of the same signed-in
// member edit the same project's context. Before the client sent `If-Match`, the
// second save silently replaced the first one's text; these cases pin the behaviour
// the release depends on:
//
//   * the second tab's save succeeds and the row really holds its text;
//   * the first tab's save is refused, its typed draft stays on screen, and nothing
//     of it reaches the row;
//   * "keep my changes" only overwrites after that explicit click;
//   * "load the latest saved version" re-bases the draft, and the next save then
//     succeeds rather than conflicting with itself;
//   * navigating to another project drops the previous project's revision, so a
//     draft is never certified against a different row.
//
// The suite creates its own throwaway account and projects through the fixture
// helpers, so it never touches seeded or real user data.

type ProjectRow = { id: string; name: string; instructions: string | null; contextNotes: string | null; status: string };

function contextCard(page: Page) {
  return page.locator(".project-context-card");
}

function instructionsField(page: Page) {
  return contextCard(page).locator("textarea").first();
}

async function saveContext(page: Page) {
  await contextCard(page).getByRole("button", { name: /save project context/i }).click();
}

async function readProject(request: Page["request"], projectId: string): Promise<ProjectRow> {
  return apiJson<ProjectRow>(request, "GET", `/api/projects/${projectId}`);
}

test("refuses the stale tab, keeps its draft, and lets the member choose the winner", async ({ authenticatedPage }) => {
  const project = await createProject(authenticatedPage, "E2E Fence Tabs");
  const url = `/projects/${project.id}`;

  // Tab A starts editing first, so it holds the revision that was live at the time.
  await authenticatedPage.goto(url);
  await expect(contextCard(authenticatedPage)).toBeVisible();
  await instructionsField(authenticatedPage).fill("Draft typed in the first tab");

  const tabB = await authenticatedPage.context().newPage();
  await tabB.goto(url);
  await expect(contextCard(tabB)).toBeVisible();
  await instructionsField(tabB).fill("Saved from the second tab");
  await saveContext(tabB);
  await expect(contextCard(tabB).locator(".form-success")).toBeVisible();
  expect((await readProject(authenticatedPage.request, project.id)).instructions).toBe("Saved from the second tab");

  // Tab A saves now. The draft it typed must survive the refusal untouched.
  await saveContext(authenticatedPage);
  await expect(contextCard(authenticatedPage).locator(".revision-conflict")).toBeVisible();
  await expect(contextCard(authenticatedPage).locator(".revision-conflict p")).toContainText(/unsaved changes have been kept/i);
  await expect(instructionsField(authenticatedPage)).toHaveValue("Draft typed in the first tab");
  expect((await readProject(authenticatedPage.request, project.id)).instructions).toBe("Saved from the second tab");

  // Explicitly choosing to keep the draft is what overwrites the other tab's save.
  await contextCard(authenticatedPage).getByRole("button", { name: /keep my changes/i }).click();
  await expect(contextCard(authenticatedPage).locator(".form-success")).toBeVisible();
  await expect(contextCard(authenticatedPage).locator(".revision-conflict")).toHaveCount(0);
  expect((await readProject(authenticatedPage.request, project.id)).instructions).toBe("Draft typed in the first tab");

  // Leaving the tab with a live conflict, then choosing the other action: the newer
  // copy is loaded and the session is re-based on it.
  await tabB.reload();
  await expect(contextCard(tabB)).toBeVisible();
  await instructionsField(tabB).fill("Second save from the second tab");
  await saveContext(tabB);
  await expect(contextCard(tabB).locator(".form-success")).toBeVisible();

  await instructionsField(authenticatedPage).fill("Draft that is now stale again");
  await saveContext(authenticatedPage);
  await expect(contextCard(authenticatedPage).locator(".revision-conflict")).toBeVisible();

  await contextCard(authenticatedPage).getByRole("button", { name: /load the latest saved version/i }).click();
  await expect(instructionsField(authenticatedPage)).toHaveValue("Second save from the second tab");
  await expect(contextCard(authenticatedPage).locator(".revision-conflict")).toHaveCount(0);
  expect((await readProject(authenticatedPage.request, project.id)).instructions).toBe("Second save from the second tab");

  // The re-based token is the live one, so the next ordinary save simply works.
  await instructionsField(authenticatedPage).fill("Saved after reloading");
  await saveContext(authenticatedPage);
  await expect(contextCard(authenticatedPage).locator(".form-success")).toBeVisible();
  await expect(contextCard(authenticatedPage).locator(".revision-conflict")).toHaveCount(0);
  expect((await readProject(authenticatedPage.request, project.id)).instructions).toBe("Saved after reloading");

  await tabB.close();
});

test("drops the previous project's revision when the member navigates to another project", async ({ authenticatedPage }) => {
  const first = await createProject(authenticatedPage, "E2E Fence Project A");
  const second = await createProject(authenticatedPage, "E2E Fence Project B");

  await authenticatedPage.goto(`/projects/${first.id}`);
  await expect(contextCard(authenticatedPage)).toBeVisible();
  await instructionsField(authenticatedPage).fill("Text that belongs to the first project");

  // Navigating mid-edit: the captured revision describes the row that was left.
  await authenticatedPage.goto(`/projects/${second.id}`);
  await expect(contextCard(authenticatedPage)).toBeVisible();
  // The second project's own saved context is on screen, not the first project's
  // unsaved draft.
  await expect(instructionsField(authenticatedPage)).toHaveValue("Use mock-safe test data only.");
  await instructionsField(authenticatedPage).fill("Text that belongs to the second project");
  await saveContext(authenticatedPage);
  await expect(contextCard(authenticatedPage).locator(".form-success")).toBeVisible();

  expect((await readProject(authenticatedPage.request, second.id)).instructions).toBe("Text that belongs to the second project");
  // The abandoned draft never leaks into the project that was edited first.
  expect((await readProject(authenticatedPage.request, first.id)).instructions).toBe("Use mock-safe test data only.");
});
