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
 * network call of its own accord, and the harness is served through request
 * interception, with one exception that is the point of the last scenario below:
 * #1267 slice 1 added a student-initiated read (the Sources tab's scholarly record
 * search), and the invariants that read has to keep - a response that arrives after
 * the student who asked for it has gone, and a query typed by a student who is no
 * longer here - are only visible in a browser that actually waits for a network
 * round trip. That scenario mocks the search route and holds one response open. The
 * rest of the read's coverage stays in `academicScholarlySearch.test.ts`,
 * `AcademicScholarlySearchResults.test.tsx`, `api.academicSources.test.ts` and the
 * API integration tests under `apps/api.Tests/AcademicSources`.
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

/** Search-shaped marks: a query one student typed, and a record that came back for it. */
const MARK_A_QUERY = "SYNTHETIC_ALPHA_PRIVATE_SEARCH_TOPIC";
const MARK_A_RECORD = "SYNTHETIC_ALPHA_DISCOVERED_RECORD";
const MARK_B_QUERY = "SYNTHETIC_BETA_PRIVATE_SEARCH_TOPIC";
const MARK_B_RECORD = "SYNTHETIC_BETA_DISCOVERED_RECORD";

type HarnessIdentity = { userId: string; workspaceId: string } | null;

const questionField = (page: Page): Locator => page.getByLabel("Research question", { exact: false });
const sourceTitleField = (page: Page): Locator => page.locator('[id$="-source-title"]');
const sourceAuthorsField = (page: Page): Locator => page.locator('[id$="-source-authors"]');
const chapterNoteField = (page: Page): Locator => page.getByLabel("Notes for section 1", { exact: false });
const saveDraftButton = (page: Page): Locator => page.getByRole("button", { name: "Save draft", exact: true });
const searchField = (page: Page): Locator => page.getByLabel("Search scholarly records", { exact: false });
const searchSubmit = (page: Page): Locator => page.getByRole("button", { name: "Search", exact: true });

/**
 * A held-open mock of the scholarly search route, for the identity-switch scenario.
 *
 * One gate per workspace lets the test decide *when* a provider's answer arrives, which is the
 * whole point: a response that lands after the student who asked for it has signed out cannot be
 * observed by a static render or a unit test with an already-resolved promise. Recorded requests
 * are what prove the negative assertions below are about behaviour rather than about a test that
 * never reached the network in the first place.
 */
type SearchMock = {
  requests: Array<{ workspaceId: string; query: string | null }>;
  gates: Record<string, { promise: Promise<void>; release: () => void }>;
  /** Which held answers actually reached the page, and which the page had already abandoned. */
  delivered: string[];
  aborted: string[];
};

function deferredGate(): SearchMock["gates"][string] {
  let release = (): void => {};
  const promise = new Promise<void>((resolve) => {
    release = resolve;
  });
  return { promise, release: () => release() };
}

function createSearchMock(workspaceIds: string[]): SearchMock {
  const gates: Record<string, { promise: Promise<void>; release: () => void }> = {};
  for (const id of workspaceIds) gates[id] = deferredGate();
  return { requests: [], gates, delivered: [], aborted: [] };
}

/** The catalog row shape the API serves, with the mark that has to stay on one side of a switch. */
function searchPayload(workspaceId: string, title: string): string {
  return JSON.stringify({
    workspaceId,
    page: 1,
    pageSize: 10,
    totalMatched: 1,
    hasMore: false,
    partial: false,
    partialNotice: null,
    sources: [
      {
        title,
        authors: ["Rahman, D."],
        publicationYear: 2021,
        sourceType: "journal_article",
        containerTitle: "Kurdish Health Policy Review",
        publisher: "Example Academic Press",
        volume: "12",
        issue: "3",
        pageRange: "110-128",
        pageCount: null,
        language: "en",
        citationCount: 14,
        doi: `10.5555/synthetic.${title.slice(-4)}`,
        isbn13: null,
        isbn10: null,
        abstract: null,
        publisherDescription: null,
        landingUrl: "https://journal.example.org/articles/synthetic-1",
        fullTextUrl: null,
        accessLevel: "metadata",
        fullTextIndicated: false,
        provenance: {
          basis: "synthetic-catalog-fixture",
          retrievedAtUtc: "2026-11-25T00:00:00.000Z",
          accessDescription: "Bibliographic metadata only",
        },
      },
    ],
    doiChecks: [],
  });
}

async function installSearchMock(page: Page, mock: SearchMock): Promise<void> {
  await page.route(/\/api\/workspaces\/[^/]+\/academic-sources\/search/, async (route) => {
    const url = new URL(route.request().url());
    const workspaceId = decodeURIComponent(url.pathname.split("/")[3] ?? "");
    const query = url.searchParams.get("q");
    mock.requests.push({ workspaceId, query });
    const gate = mock.gates[workspaceId];
    const record = workspaceId === studentA.workspaceId ? MARK_A_RECORD : MARK_B_RECORD;
    if (!gate) {
      await route.fulfill({ status: 403, contentType: "application/json", body: JSON.stringify({ code: "FORBIDDEN" }) });
      return;
    }
    // Hold the answer until the scenario says the principal has already changed.
    await gate.promise;
    try {
      await route.fulfill({ status: 200, contentType: "application/json", body: searchPayload(workspaceId, record) });
      mock.delivered.push(workspaceId);
    } catch {
      // The page stopped caring before the answer could be handed over, which is the correct
      // outcome for a request whose student is gone: the panel aborts on dispose. Recorded rather
      // than asserted, because whether the abort wins the race against this write is the
      // browser's scheduling, not the component's contract.
      mock.aborted.push(workspaceId);
    }
  });
}

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
    const requestUrl = new URL(route.request().url());
    if (requestUrl.pathname.startsWith("/api/")) {
      // The scholarly-search mock answers these; the asset handler must not turn an API read into
      // a 404, whichever order the two route handlers are registered in.
      await route.fallback();
      return;
    }
    const relative = decodeURIComponent(requestUrl.pathname.replace(/^\//, ""));
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

async function openHarness(page: Page, identity: HarnessIdentity, mode: "studio" | "workspace", search?: SearchMock): Promise<void> {
  await page.addInitScript((initial) => {
    window.__academicInitialIdentity = initial;
  }, identity);
  // Registered first so it wins if Playwright resolves matching routes in order; the asset handler
  // below also declines to handle `/api/` paths, so the order cannot decide the outcome either way.
  if (search) await installSearchMock(page, search);
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

    /**
     * Reviewer residual R4 (issuecomment-7148092453): every other scenario in this file never
     * presses Search, so the one student-initiated read this slice added had no browser coverage
     * of the case that matters for it - an answer that arrives after the student who asked has
     * gone. The unit tests hold the sequencer and the abort (`academicScholarlySearch.test.ts:337`,
     * `:381-397`); a static render cannot see a request in flight, and only a real page shows that
     * the record never paints, the query never carries over, and nothing is persisted.
     *
     * Runs in both mount modes. `studio` is the shipped container, whose identity-keyed subtree
     * remounts the panel; `workspace` is the same workspace without that key, where only the
     * panel's own fence stands between the two students.
     */
    test("never renders a search answer from the student who is no longer signed in", async ({ page }) => {
      const mock = createSearchMock([studentA.workspaceId, studentB.workspaceId]);
      await openHarness(page, studentA, mode, mock);
      await openTab(page, /^Sources/);

      // Student A starts a search and leaves before the catalogs answer.
      await searchField(page).fill(MARK_A_QUERY);
      await searchSubmit(page).click();
      await expect.poll(() => mock.requests.length).toBe(1);
      expect(mock.requests[0]).toEqual({ workspaceId: studentA.workspaceId, query: MARK_A_QUERY });
      await expect(page.getByText("Searching scholarly records", { exact: false })).toBeVisible();
      await setIdentity(page, studentB);

      // A's answer is released only now, into a page that belongs to somebody else.
      mock.gates[studentA.workspaceId].release();
      await openTab(page, /^Sources/);
      await expect(page.getByText(MARK_A_RECORD)).toHaveCount(0);
      // The query A typed was A's too: the box is empty rather than pre-filled with their topic.
      await expect(searchField(page)).toHaveValue("");
      // And nothing about it was written under the new principal's storage keys.
      const afterSwitch = await storageDump(page);
      expect(afterSwitch).not.toContain(MARK_A_RECORD);
      expect(afterSwitch).not.toContain(MARK_A_QUERY);
      // A's request must not have been re-issued under B's workspace by way of "retrying" it.
      expect(mock.requests.filter((entry) => entry.query === MARK_A_QUERY).map((entry) => entry.workspaceId))
        .toEqual([studentA.workspaceId]);

      // The same shape in the other direction: B's answer lands after B has left.
      await searchField(page).fill(MARK_B_QUERY);
      await searchSubmit(page).click();
      await expect.poll(() => mock.requests.length).toBe(2);
      expect(mock.requests[1]).toEqual({ workspaceId: studentB.workspaceId, query: MARK_B_QUERY });
      await setIdentity(page, studentA);
      mock.gates[studentB.workspaceId].release();
      await openTab(page, /^Sources/);
      await expect(page.getByText(MARK_B_RECORD)).toHaveCount(0);
      await expect(searchField(page)).toHaveValue("");
      expect(await storageDump(page)).not.toContain(MARK_B_RECORD);

      // Positive control, so the absences above are a fence rather than a mock that never answers:
      // a search that starts and finishes under one student does reach the page.
      await searchField(page).fill(MARK_A_QUERY);
      await searchSubmit(page).click();
      await expect.poll(() => mock.requests.length).toBe(3);
      await expect(page.getByText(MARK_A_RECORD).first()).toBeVisible();
      await expect(page.getByText(MARK_B_RECORD)).toHaveCount(0);
    });
  });
}
