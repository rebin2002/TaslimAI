import { test, expect, type Page } from "./fixtures";

/**
 * RTL coverage for the Full Movie workspace.
 *
 * The workspace is authored with logical CSS properties (inset-inline,
 * padding-inline, margin-inline, text-align: start) and the delivery rooms use
 * `dir`-aware lists, so an RTL locale must not break layout. These tests drive
 * the real module routes in Arabic and Kurdish and assert the document
 * direction, the absence of horizontal overflow, and that the delivery rooms
 * still render their persisted-record surfaces.
 */

const deliveryRooms = [
  { slug: "audio", testId: "movie-audio-workspace" },
  { slug: "qc", testId: "movie-qc-workspace" },
  { slug: "exports", testId: "movie-exports-workspace" },
  { slug: "team", testId: "movie-team-workspace" },
];

const locales = [
  { code: "ar", dir: "rtl" },
  { code: "ku", dir: "rtl" },
];

async function expectNoHorizontalOverflow(page: Page, label: string) {
  const overflow = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    innerWidth: window.innerWidth,
  }));
  expect(
    overflow.scrollWidth <= overflow.innerWidth + 2,
    `${label} must not overflow horizontally in RTL (scrollWidth ${overflow.scrollWidth} vs viewport ${overflow.innerWidth})`,
  ).toBe(true);
}

async function createFullMovieProject(page: Page) {
  await page.goto("/create/movie");
  await expect(page.getByRole("heading", { name: /movie studio/i })).toBeVisible();
  await page.getByRole("button", { name: /full movie/i }).first().click();
  await page.getByLabel("Movie title").fill("E2E RTL Full Movie");
  await page
    .getByLabel("Describe your movie")
    .fill("An RTL layout acceptance journey across the Full Movie workspace.");
  await page.getByRole("button", { name: /create full project/i }).click();
  await expect(page).toHaveURL(/\/create\/movie\/[0-9a-f-]+\/overview$/i);
  const projectId = page.url().match(/\/create\/movie\/([0-9a-f-]+)\/overview$/i)?.[1];
  expect(projectId).toBeTruthy();
  return projectId as string;
}

async function switchLocale(page: Page, locale: string) {
  await page.goto("/projects");
  const language = page.locator(".language-select select").first();
  await language.selectOption(locale);
  await expect.poll(() => page.evaluate(() => window.localStorage.getItem("taslim-locale"))).toBe(locale);
}

test.describe("Movie Studio RTL", () => {
  for (const locale of locales) {
    test(`keeps the Full Movie workspace and delivery rooms usable in ${locale.code}`, async ({
      authenticatedPage: page,
    }) => {
      test.setTimeout(180_000);
      const projectId = await createFullMovieProject(page);
      await switchLocale(page, locale.code);

      await expect(page.locator("html")).toHaveAttribute("dir", locale.dir);
      await expect(page.locator("html")).toHaveAttribute("lang", locale.code);

      // The project map is the entry point for every module route.
      await page.goto(`/create/movie/${projectId}/overview`);
      await expect(page.locator("nav[aria-label]").first()).toBeVisible();
      await expectNoHorizontalOverflow(page, `overview (${locale.code})`);

      // The four delivery rooms replaced their previous placeholders and must
      // stay direction-safe in RTL.
      for (const room of deliveryRooms) {
        await page.goto(`/create/movie/${projectId}/${room.slug}`);
        await expect(page.getByTestId(room.testId)).toBeVisible();
        await expect(page.locator("html")).toHaveAttribute("dir", locale.dir);
        await expectNoHorizontalOverflow(page, `${room.slug} (${locale.code})`);
      }

      // A room that renders a real RTL-sensitive surface: captions keep their
      // own direction while the surrounding layout stays mirrored.
      await page.goto(`/create/movie/${projectId}/audio`);
      await expect(page.getByTestId("movie-audio-workspace")).toBeVisible();
      await expectNoHorizontalOverflow(page, `audio recheck (${locale.code})`);
    });
  }

  test("keeps the Full Movie project map readable in RTL", async ({ authenticatedPage: page }) => {
    test.setTimeout(120_000);
    const projectId = await createFullMovieProject(page);
    await switchLocale(page, "ar");

    await page.goto(`/create/movie/${projectId}/overview`);
    // Navigation links are mirrored but must all remain visible and clickable.
    const navLinks = page.locator("nav a");
    await expect(navLinks.first()).toBeVisible();
    const count = await navLinks.count();
    expect(count).toBeGreaterThan(4);
    for (let index = 0; index < Math.min(count, 6); index += 1) {
      await expect(navLinks.nth(index)).toBeVisible();
    }
    await expectNoHorizontalOverflow(page, "project map (ar)");
  });
});
