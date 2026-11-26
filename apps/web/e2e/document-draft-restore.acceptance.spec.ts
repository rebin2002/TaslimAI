import { test, expect } from "./fixtures";

/**
 * Browser acceptance for the unsaved document brief across an editor tab
 * switch (lane #962).
 *
 * An unsent Document Studio brief - description, guided choices, everything
 * typed into the compose form - is kept browser-local until a generation job
 * is submitted; before this fix, switching tabs away and back (unmount ->
 * remount) returned the user to an empty editor with the work discarded.
 * These tests drive the real editor in a real authenticated session at each
 * shipping locale (English, Arabic, Sorani) and prove:
 *
 *   1. the unsent brief survives the tab switch and comes back verbatim;
 *   2. the localized "restored" notice appears in the language the user is
 *      reading the studio in (and only then);
 *   3. the document never reaches the generation API - the draft is private
 *      to the browser tab until the user presses Generate.
 *
 * No provider is involved: the job-submission route is only counted, never
 * fulfilled, and the suite's API runs with Ai__OpenAI__Enabled=false.
 */
const brief = "Retail pilot launch brief typed into the editor but never submitted.";

// Exact copies from apps/web/src/lib/i18n.ts, keyed per locale. The parity
// unit test pins that all three catalogs carry document.draftRestored; this
// suite pins that the browser actually renders the right one for the user's
// language and writing direction.
const draftRestoredCopy = {
  en: "Unsent brief restored from this browser tab.",
  ar: "تمت استعادة موجزك غير المُرسَل من هذا التبويب.",
  ku: "پێشنووسی نەنێردراوت لەم پەڕەیەوە گەڕێندرایەوە.",
} as const;

test.describe("Document Studio unsaved brief across a tab switch", () => {
  for (const profile of [
    { locale: "en", lang: "en", dir: "ltr" },
    { locale: "ar", lang: "ar", dir: "rtl" },
    { locale: "ku", lang: "ckb", dir: "rtl" },
  ] as const) {
    test(`restores the unsent brief and names it in ${profile.locale}`, async ({ authenticatedPage: page }) => {
      let jobSubmissions = 0;
      await page.route("**/api/document-generation/jobs", async (route) => {
        jobSubmissions += 1;
        await route.abort();
      });
      await page.addInitScript((locale) => {
        window.localStorage.setItem("taslim-locale", locale);
      }, profile.locale);

      // First visit: nothing was left unsent in this tab, so the editor opens
      // blank and says nothing about a restoration.
      await page.goto("/create/document");
      await expect(page.locator("html")).toHaveAttribute("lang", profile.lang);
      await expect(page.locator("html")).toHaveAttribute("dir", profile.dir);
      const briefField = page.locator("#document-brief");
      await expect(briefField).toHaveValue("");
      await expect(page.locator("[data-document-draft-restored]")).toHaveCount(0);

      // Compose work without submitting: a brief and a guided choice that the
      // restored editor must carry back.
      await briefField.fill(brief);
      await page.locator("#document-output-format").selectOption("pdf");

      // The tab switch: another studio tab unmounts the editor, coming back
      // remounts it. sessionStorage is per tab, so this is exactly the
      // away-and-return a user performs.
      await page.goto("/assets");
      await page.goto("/create/document");

      await expect(briefField).toHaveValue(brief);
      await expect(page.locator("#document-output-format")).toHaveValue("pdf");
      await expect(page.locator("html")).toHaveAttribute("dir", profile.dir);

      // The restoration is announced in the user's language, as a polite
      // status message rather than a surprise.
      const notice = page.locator("[data-document-draft-restored]");
      await expect(notice).toHaveCount(1);
      await expect(notice).toHaveAttribute("role", "status");
      await expect(notice).toContainText(draftRestoredCopy[profile.locale]);

      // The unsent brief stayed browser-local: not one generation request.
      expect(jobSubmissions).toBe(0);
    });
  }
});
