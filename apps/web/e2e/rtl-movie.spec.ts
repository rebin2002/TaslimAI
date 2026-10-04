import type { Page } from "@playwright/test";
import { test, expect } from "./fixtures";

/**
 * RTL coverage for the Full Movie workspace.
 *
 * The workspace is authored with logical CSS properties (inset-inline,
 * padding-inline, margin-inline, text-align: start) and the delivery rooms use
 * `dir`-aware lists, so a right-to-left locale must not break layout. These
 * tests select a right-to-left locale through the language control on each
 * route and assert the document direction, the absence of horizontal overflow,
 * and that the delivery rooms render their persisted-record surfaces.
 */

const deliveryRooms = [
  { slug: "audio", testId: "movie-audio-workspace" },
  { slug: "qc", testId: "movie-qc-workspace" },
  { slug: "exports", testId: "movie-exports-workspace" },
  { slug: "team", testId: "movie-team-workspace" },
];
const deepModules = [
  "overview",
  "production-kit",
  "story",
  "cast",
  "world",
  "scenes",
  "storyboard",
  "production",
  "selects",
  "edit",
  "audio",
  "qc",
  "exports",
  "team",
] as const;

const locales = ["ar", "ku"] as const;

async function expectDirection(page: Page, dir: string, label: string) {
  await expect
    .poll(() => page.evaluate(() => document.documentElement.dir), {
      message: `${label} must resolve to ${dir}`,
      timeout: 30_000,
    })
    .toBe(dir);
}

/** Select a locale through the header control and confirm it is applied. */
async function applyLocale(page: Page, locale: string, label: string) {
  const language = page.locator(".language-select select").first();
  await language.selectOption(locale);
  await expect
    .poll(() => page.evaluate(() => window.localStorage.getItem("taslim-locale")))
    .toBe(locale);
  await expectDirection(page, locale === "en" ? "ltr" : "rtl", label);
  await expect(page.locator("html")).toHaveAttribute("lang", locale);
}

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
  await page.getByRole("tab", { name: /full movie/i }).first().click();
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

test.describe("Movie Studio RTL", () => {
  for (const locale of locales) {
    test(`keeps the Full Movie workspace and delivery rooms usable in ${locale}`, async ({
      authenticatedPage: page,
    }) => {
      test.setTimeout(300_000);
      const projectId = await createFullMovieProject(page);

      // The project map is the entry point for every module route.
      await page.goto(`/create/movie/${projectId}/overview`);
      await expect(page.locator("nav[aria-label]").first()).toBeVisible();
      await applyLocale(page, locale, `overview (${locale})`);
      await expectNoHorizontalOverflow(page, `overview (${locale})`);

      // The four delivery rooms replaced their previous placeholders and must
      // stay direction-safe in RTL.
      for (const room of deliveryRooms) {
        await page.goto(`/create/movie/${projectId}/${room.slug}`);
        await expect(page.getByTestId(room.testId)).toBeVisible();
        await applyLocale(page, locale, `${room.slug} (${locale})`);
        await expectNoHorizontalOverflow(page, `${room.slug} (${locale})`);
      }
    });
  }

  test("keeps the Full Movie project map readable in RTL", async ({ authenticatedPage: page }) => {
    test.setTimeout(180_000);
    const projectId = await createFullMovieProject(page);

    await page.goto(`/create/movie/${projectId}/overview`);
    await applyLocale(page, "ar", "project map (ar)");
    // Navigation links are mirrored but must all remain visible.
    const navLinks = page.locator("nav a");
    await expect(navLinks.first()).toBeVisible();
    const count = await navLinks.count();
    expect(count).toBeGreaterThan(4);
    for (let index = 0; index < Math.min(count, 6); index += 1) {
      await expect(navLinks.nth(index)).toBeVisible();
    }
    await expectNoHorizontalOverflow(page, "project map (ar)");
  });

  for (const locale of locales) {
    test(`covers every deep module body without RTL overflow in ${locale}`, async ({ authenticatedPage: page }) => {
      test.setTimeout(300_000);
      const projectId = await createFullMovieProject(page);
      for (const slug of deepModules) {
        await page.goto(`/create/movie/${projectId}/${slug}`);
        await expect(page.locator("main.movie-workspace-main")).toBeVisible({ timeout: 30_000 });
        await applyLocale(page, locale, `${slug} (${locale})`);
        await expectNoHorizontalOverflow(page, `${slug} (${locale})`);
        await expect(page.locator("main.movie-workspace-main")).toBeVisible();
      }
    });
  }
});
