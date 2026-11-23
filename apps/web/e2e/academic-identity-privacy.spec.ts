/**
 * Issue #1259 (Academic Research Studio) — PR #1261 mounted-identity privacy regression.
 *
 * Operator review of `ec623a4b` found that the Phase A container initialized its
 * workspace state once and then, on a principal change, only called `setState` when
 * `readWorkspaceDraft` returned a truthy draft. Consequence for a mounted instance:
 * when the signed-in student changed to one with no draft - or to no principal at
 * all - the previous student's brief, sources, evidence and chapter notes stayed on
 * screen, and "Save draft" could write that payload under the new principal's
 * storage key. Static SSR rendering and storage-key unit tests cannot see this,
 * because neither mounts the tree and then changes the identity underneath it.
 *
 * These tests mount the real component in a real browser and switch the principal,
 * through the host shape the component itself documents ("Optional host override for
 * tests and embedding"). Each scenario runs twice: against the shipped container,
 * whose identity-keyed subtree is the primary protection, and against the inner
 * workspace rendered without that key, where only the fail-closed principal fence
 * remains.
 *
 * Synthetic data only, and nothing leaves the page: the academic surface makes no
 * network call, and the harness is served through request interception.
 */
import { execSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { expect, test, type Locator, type Page } from "@playwright/test";

const webRoot = process.cwd();
const harnessCache = path.resolve(webRoot, "../../node_modules/.cache/taslim-academic-harness");
// Playwright runs specs in parallel workers, so each worker builds into its own
// directory: two concurrent `vite build` calls must not write one output tree.
const harnessOut = path.join(harnessCache, `worker-${process.pid}`);
const harnessOrigin = "https://academic-harness.test";

/** Synthetic identities. Both are fictitious; neither names a real account. */
const studentA = { userId: "aaaaaaaa-1111-4111-8111-111111111111", workspaceId: "awwwwwww-1111-4111-8111-111111111111" };
const studentB = { userId: "bbbbbbbb-2222-4222-8222-222222222222", workspaceId: "bwwwwwww-2222-4222-8222-222222222222" };

const MARK_A_QUESTION = "SYNTHETIC_ALPHA_PRIVATE_QUESTION";
const MARK_A_SOURCE = "SYNTHETIC_ALPHA_PRIVATE_SOURCE";
const MARK_A_NOTE = "SYNTHETIC_ALPHA_PRIVATE_CHAPTER_NOTE";
const MARK_B_QUESTION = "SYNTHETIC_BETA_PRIVATE_QUESTION";
const MARK_B_SOURCE = "SYNTHETIC_BETA_PRIVATE_SOURCE";

type HarnessIdentity = { userId: string; workspaceId: string } | null;

const questionField = (page: Page): Locator => page.getByLabel("Research question", { exact: false });
const sourceTitleField = (page: Page): Locator => page.locator('[id$="-source-title"]');
const sourceAuthorsField = (page: Page): Locator => page.locator('[id$="-source-authors"]');
const chapterNoteField = (page: Page): Locator => page.getByLabel("Notes for section 1", { exact: false });
const saveDraftButton = (page: Page): Locator => page.getByRole("button", { name: "Save draft", exact: true });

/** Add a verified source: the default type is author-required, so fill both fields. */
async function addSource(page: Page, title: string, authors: string): Promise<void> {
  await sourceTitleField(page).fill(title);
  await sourceAuthorsField(page).fill(authors);
  await page.getByRole("button", { name: "Add to library", exact: true }).click();
  await expect(page.getByText(title).first()).toBeVisible();
}

function openTab(page: Page, name: RegExp): Promise<void> {
  return page.getByRole("tab", { name }).click();
}

function buildHarness(): void {
  execSync(
    `npx vite build --config e2e/academic-identity-mount/vite.harness.config.ts --outDir ${harnessOut} --emptyOutDir`,
    { cwd: webRoot, stdio: "pipe" },
  );
}

async function serveHarness(page: Page): Promise<void> {
  await page.route(`${harnessOrigin}/**`, async (route) => {
    const relative = decodeURIComponent(new URL(route.request().url()).pathname.replace(/^\//, ""));
    const file = path.join(harnessOut, relative);
    if (!relative || !file.startsWith(harnessOut) || !existsSync(file)) {
      await route.fulfill({ status: 404, contentType: "text/plain", body: "harness asset missing" });
      return;
    }
    const contentType = relative.endsWith(".html")
      ? "text/html"
      : relative.endsWith(".js")
        ? "text/javascript"
        : relative.endsWith(".css")
          ? "text/css"
          : "application/octet-stream";
    await route.fulfill({ status: 200, contentType, body: readFileSync(file) });
  });
}

async function openHarness(page: Page, identity: HarnessIdentity, mode: "studio" | "workspace"): Promise<void> {
  await page.addInitScript((initial) => {
    window.__academicInitialIdentity = initial;
  }, identity);
  await serveHarness(page);
  await page.goto(`${harnessOrigin}/harness.html?mode=${mode}`);
  await expect(page.getByRole("heading", { name: "Academic Research Studio", exact: true })).toBeVisible();
  await expect(questionField(page)).toBeVisible();
}

function setIdentity(page: Page, identity: HarnessIdentity): Promise<unknown> {
  return page.evaluate((next) => window.__academicHarness?.setIdentity(next), identity);
}

/** Every persisted draft byte, so a cross-identity write cannot hide behind a key name. */
async function storageDump(page: Page): Promise<string> {
  return page.evaluate(() =>
    JSON.stringify(Object.keys(window.sessionStorage).map((key) => [key, window.sessionStorage.getItem(key)])),
  );
}

/**
 * Drafts the storage layer itself declares to belong to a user. The envelope owner is
 * authoritative here: the previous student's own draft is supposed to survive a switch
 * untouched, so a leak is a blob stored under the *new* principal's ownership.
 */
async function draftsOwnedBy(page: Page, userId: string): Promise<Array<{ key: string; value: string }>> {
  const pairs = JSON.parse(await storageDump(page)) as Array<[string, string | null]>;
  return pairs.flatMap(([key, value]) => {
    if (!value) return [];
    let owner: unknown;
    try {
      owner = (JSON.parse(value) as { u?: unknown }).u;
    } catch {
      return [];
    }
    return owner === userId ? [{ key, value }] : [];
  });
}

async function saveDraft(page: Page): Promise<void> {
  await saveDraftButton(page).click();
  await expect(page.getByText("Draft saved on this device", { exact: false })).toBeVisible();
}

/** Student A's work, spanning three sub-components plus one unsubmitted form. */
async function fillStudentAWork(page: Page): Promise<void> {
  await questionField(page).fill(MARK_A_QUESTION);
  await openTab(page, /^Sources/);
  await addSource(page, MARK_A_SOURCE, "Alpha, A.");
  await openTab(page, /^Chapters/);
  await chapterNoteField(page).fill(MARK_A_NOTE);
}

async function expectNoAlphaData(page: Page): Promise<void> {
  await expect(page.getByText(MARK_A_QUESTION)).toHaveCount(0);
  await expect(page.getByText(MARK_A_SOURCE)).toHaveCount(0);
  await expect(page.getByText(MARK_A_NOTE)).toHaveCount(0);
  await expect(questionField(page)).toHaveValue("");
}

for (const mode of ["studio", "workspace"] as const) {
  test.describe(`academic workspace identity binding (${mode})`, () => {
    test.beforeAll(() => {
      buildHarness();
      if (!existsSync(path.join(harnessOut, "harness.html"))) {
        throw new Error(`harness build produced no output in ${harnessOut}`);
      }
    });

    test("shows the next student an empty workspace when they have no draft, and never saves the previous student's work under their key", async ({ page }) => {
      await openHarness(page, studentA, mode);
      await fillStudentAWork(page);
      await saveDraft(page);
      expect(await storageDump(page)).toContain(MARK_A_QUESTION);

      // Same tab, new principal, no draft of their own.
      await setIdentity(page, studentB);
      await expectNoAlphaData(page);

      // Committed records from the last session must be gone, not merely hidden.
      await openTab(page, /^Sources/);
      await expect(page.getByText("No sources yet", { exact: false })).toBeVisible();
      // The nested source form's unsubmitted buffers belonged to the last student too.
      await expect(sourceTitleField(page)).toHaveValue("");

      // A save as this principal must not persist anything belonging to the last one.
      await saveDraft(page);
      const betaDrafts = await draftsOwnedBy(page, studentB.userId);
      expect(betaDrafts.length, "the new principal should have received their own draft record").toBeGreaterThan(0);
      for (const { key, value } of betaDrafts) {
        expect(value, `draft saved under the new principal carries the last student's work: ${key}`).not.toContain(MARK_A_QUESTION);
        expect(value, `draft saved under the new principal carries the last student's work: ${key}`).not.toContain(MARK_A_SOURCE);
        expect(value, `draft saved under the new principal carries the last student's work: ${key}`).not.toContain(MARK_A_NOTE);
      }
      // The previous student's own draft must survive untouched, not be relocated.
      const alphaDrafts = await draftsOwnedBy(page, studentA.userId);
      expect(alphaDrafts.map((entry) => entry.value).join("")).toContain(MARK_A_QUESTION);
    });

    test("restores the draft that belongs to the principal now signed in", async ({ page }) => {
      await openHarness(page, studentA, mode);
      await questionField(page).fill(MARK_A_QUESTION);
      await saveDraft(page);

      await setIdentity(page, studentB);
      await expect(questionField(page)).toHaveValue("");
      await questionField(page).fill(MARK_B_QUESTION);
      await openTab(page, /^Sources/);
      await addSource(page, MARK_B_SOURCE, "Beta, B.");
      await saveDraft(page);

      await setIdentity(page, studentA);
      await expect(questionField(page)).toHaveValue(MARK_A_QUESTION);
      await expect(page.getByText(MARK_B_SOURCE)).toHaveCount(0);

      await setIdentity(page, studentB);
      await expect(questionField(page)).toHaveValue(MARK_B_QUESTION);
      await openTab(page, /^Sources/);
      await expect(page.getByText(MARK_B_SOURCE).first()).toBeVisible();
      await expect(page.getByText(MARK_A_SOURCE)).toHaveCount(0);
    });

    test("renders nothing private once the principal is gone, and keeps the owner's draft intact for their return", async ({ page }) => {
      await openHarness(page, studentA, mode);
      await fillStudentAWork(page);
      await saveDraft(page);

      await setIdentity(page, null);
      await expectNoAlphaData(page);
      // Clearing the view must not destroy the owner's stored draft.
      expect(await storageDump(page)).toContain(MARK_A_QUESTION);

      await setIdentity(page, studentA);
      await expect(questionField(page)).toHaveValue(MARK_A_QUESTION);
      await openTab(page, /^Chapters/);
      await expect(chapterNoteField(page)).toHaveValue(MARK_A_NOTE);
    });

    test("survives rapid identity switching without mixing two students' records", async ({ page }) => {
      await openHarness(page, studentA, mode);
      await fillStudentAWork(page);
      await saveDraft(page);
      await setIdentity(page, studentB);
      await questionField(page).fill(MARK_B_QUESTION);
      await saveDraft(page);

      // Churn inside one task: only the final principal may be visible.
      await page.evaluate(async () => {
        const api = window.__academicHarness;
        api?.setIdentity({ userId: "aaaaaaaa-1111-4111-8111-111111111111", workspaceId: "awwwwwww-1111-4111-8111-111111111111" });
        api?.setIdentity({ userId: "bbbbbbbb-2222-4222-8222-222222222222", workspaceId: "bwwwwwww-2222-4222-8222-222222222222" });
        api?.setIdentity({ userId: "aaaaaaaa-1111-4111-8111-111111111111", workspaceId: "awwwwwww-1111-4111-8111-111111111111" });
        await Promise.resolve();
      });

      await expect(questionField(page)).toHaveValue(MARK_A_QUESTION);
      await expect(page.getByText(MARK_B_QUESTION)).toHaveCount(0);
      await openTab(page, /^Sources/);
      await expect(page.getByText(MARK_A_SOURCE).first()).toBeVisible();
      await expect(page.getByText(MARK_B_SOURCE)).toHaveCount(0);

      // Each draft stays single-tenant: no stored blob may carry both students' marks.
      const pairs = JSON.parse(await storageDump(page)) as Array<[string, string | null]>;
      let alphaDrafts = 0;
      let betaDrafts = 0;
      for (const [key, value] of pairs) {
        if (!value) continue;
        const holdsAlpha = value.includes(MARK_A_QUESTION) || value.includes(MARK_A_SOURCE) || value.includes(MARK_A_NOTE);
        const holdsBeta = value.includes(MARK_B_QUESTION) || value.includes(MARK_B_SOURCE);
        expect(holdsAlpha && holdsBeta, `one draft blob carries both students: ${key}`).toBe(false);
        if (holdsAlpha) alphaDrafts += 1;
        if (holdsBeta) betaDrafts += 1;
      }
      expect(alphaDrafts).toBeGreaterThan(0);
      expect(betaDrafts).toBeGreaterThan(0);
    });

    /**
     * The other direction of the same fence: an identity change must be recognised from
     * the scope it carries, not from the object reference. A host may rebuild
     * `{ userId, workspaceId }` on each render, and treating that as a new student would
     * silently wipe the work of the student who is still signed in.
     */
    test("keeps the current student's work when the host re-renders with an equal scope", async ({ page }) => {
      await openHarness(page, studentA, mode);
      await questionField(page).fill(MARK_A_QUESTION);
      await openTab(page, /^Sources/);
      await addSource(page, MARK_A_SOURCE, "Alpha, A.");
      await openTab(page, /^Chapters/);
      await chapterNoteField(page).fill(MARK_A_NOTE);

      // Same user, same workspace, a brand-new object - e.g. a session refresh.
      await setIdentity(page, { ...studentA });

      await expect(questionField(page)).toHaveValue(MARK_A_QUESTION);
      await openTab(page, /^Sources/);
      await expect(page.getByText(MARK_A_SOURCE).first()).toBeVisible();
      await openTab(page, /^Chapters/);
      await expect(chapterNoteField(page)).toHaveValue(MARK_A_NOTE);
    });
  });
}
