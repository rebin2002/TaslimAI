import { test, expect } from "./fixtures";
import type { Locator, Page } from "@playwright/test";

// Independent BROWSER acceptance for draft PR #1165 (optional Product Ads
// logo/photo file-name aria-describedby). #1165 ships its own unit evidence
// (`renderToStaticMarkup` + source guards); #785/#798 pin logo survival and
// removal semantics, and #937 (merged) introduced the described-by helper -
// all as unit tests. Nothing in the e2e directory has ever read the
// `aria-describedby` attribute, so the association PR #1165 adds between a
// chosen file name and its upload control has no real-Chromium evidence.
//
// This file only reads what a screen reader consumes: the accessible NAME
// (label) and DESCRIPTION (help -> attached file name, in reading order) of
// each file input, computed by Playwright's W3C accname implementation in the
// browser, plus the described-by token list itself. Per the gap it pins:
//   * English / Arabic / Kurdish copy reaches the announcement;
//   * keyboard focus reaches the photo control and its focused element
//     carries the attachment (screen-reader proxy: computed description);
//   * replacement swaps the announced name with no stale text left;
//   * removal drops both the row and its token, and the other slot is
//     untouched (no cross-slot staleness);
//   * a restored draft never leaves a `-photo-file`/`-logo-file` token
//     pointing at a row the render no longer owns (absent DOM target);
//   * every token always resolves to a non-empty in-DOM element, at 320px
//     included, where the rows must stay visible with no horizontal overflow.
//
// Test-only: no component, CSS, locale or state file is touched. Files are
// synthetic in-memory PNGs handed to the input via setInputFiles; the wizard
// is local-only, so no upload, provider call or customer data exists here.

const onePixelPng = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aGQAAAAASUVORK5CYII=",
  "base64",
);

function pngFile(name: string) {
  return { name, mimeType: "image/png", buffer: onePixelPng };
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

async function describedByTokens(input: Locator): Promise<string[]> {
  const value = await input.getAttribute("aria-describedby");
  return (value ?? "").split(/\s+/).filter(Boolean);
}

// Resolve every described-by token of one input against the live document:
// a token with no element is an absent DOM target; an element with no text is
// an announcement that would speak nothing. Both are contract violations the
// helper's comment calls out ("worse than a shorter list").
async function resolveTargets(page: Page, input: Locator): Promise<Array<{ token: string; exists: boolean; text: string }>> {
  const tokens = await describedByTokens(input);
  return page.evaluate((ids) =>
    ids.map((token) => {
      const el = document.getElementById(token);
      return { token, exists: el !== null, text: el?.textContent ?? "" };
    }), tokens,
  );
}

const profiles = [
  {
    locale: "en",
    photoLabel: "Product photo (required)",
    logoLabel: "Brand logo (optional)",
    photoFirst: "product-front.png",
    photoSecond: "product-back.png",
    logoFile: "brand-mark.png",
    staleLogoName: "brand-mark.png",
  },
  {
    locale: "ar",
    photoLabel: "صورة المنتج (مطلوبة)",
    logoLabel: "شعار العلامة التجارية (اختياري)",
    photoFirst: "منتج-أمامي.png",
    photoSecond: "منتج-خلفي.png",
    logoFile: "شعار-العلامة.png",
    staleLogoName: "شعار-العلامة.png",
  },
  {
    locale: "ku",
    photoLabel: "وێنەی بەرهەم (پێویستە)",
    logoLabel: "لۆگۆی براند (ئارەزوومەندانە)",
    photoFirst: "بەرهەم-پێش.png",
    photoSecond: "بەرهەم-دوا.png",
    logoFile: "لۆگۆی-براند.png",
    staleLogoName: "لۆگۆی-براند.png",
  },
] as const;

async function openWizard(page: Page, locale: string): Promise<{ photo: Locator; logo: Locator; photoHelp: Locator; logoHelp: Locator; nameField: Locator }> {
  await page.addInitScript((value) => window.localStorage.setItem("taslim-locale", value), locale);
  await page.goto("/create/product-ad");
  const nameField = page.locator('input[id$="-product-name"]');
  await expect(nameField).toBeVisible();
  // The wizard shell is the only lang-tagged section; waiting on it proves the
  // session really reached the localized unmasked form (the #1022 contract).
  await expect(page.locator("section[lang]")).toBeVisible();
  const photo = page.locator('input[id$="-product-photo"]');
  const logo = page.locator('input[id$="-brand-logo"]');
  return { photo, logo, photoHelp: page.locator('small[id$="-photo-help"]'), logoHelp: page.locator('small[id$="-logo-help"]'), nameField };
}

test.describe("Product Ad attached-file name reaches its upload control (PR #1165, browser acceptance)", () => {
  for (const profile of profiles) {
    test(`announces attached name via accessible description across keyboard, replacement and removal (${profile.locale})`, async ({ authenticatedPage: page }) => {
      const unexpectedProviderRequests: string[] = [];
      page.on("request", (request) => {
        if (/sunoapi|runway|\/api\/(generation|payments|checkout|provider|assets\/upload)/i.test(request.url())) {
          unexpectedProviderRequests.push(request.url());
        }
      });

      const { photo, logo, photoHelp, logoHelp, nameField } = await openWizard(page, profile.locale);

      // Accessible NAME of each control is the localized label; a screen
      // reader must not need sight to tell the required photo from the
      // optional logo.
      await expect(photo).toHaveAccessibleName(new RegExp(escapeRegExp(profile.photoLabel)));
      await expect(logo).toHaveAccessibleName(profile.logoLabel);

      // Before any choice the file row must not exist, must not be tokened,
      // and the description is the help sentence alone (attached: false).
      await expect(page.locator('small[id$="-photo-file"]')).toHaveCount(0);
      await expect(page.locator('small[id$="-logo-file"]')).toHaveCount(0);
      let photoTokens = await describedByTokens(photo);
      expect(photoTokens.some((token) => token.endsWith("-photo-file"))).toBe(false);
      let logoTokens = await describedByTokens(logo);
      expect(logoTokens.some((token) => token.endsWith("-logo-file"))).toBe(false);

      // Keyboard path: tab from the name field (benefit sits between) to the
      // photo control; the focused element is the one carrying the contract.
      await nameField.focus();
      await page.keyboard.press("Tab");
      await expect(page.locator('textarea[id$="-benefit"]')).toBeFocused();
      await page.keyboard.press("Tab");
      await expect(photo).toBeFocused();
      expect(await photo.evaluate((el) => el.id)).toBe(await nameField.evaluate((el) => el.id.replace("-product-name", "-product-photo")));

      // Attach the photo: the visible row and the announcement agree.
      await photo.setInputFiles(pngFile(profile.photoFirst));
      const photoHelpText = (await photoHelp.textContent()) ?? "";
      const photoRow = page.locator('small[id$="-photo-file"]');
      await expect(photoRow).toHaveText(profile.photoFirst);
      await expect(photoRow).toHaveAttribute("dir", "auto");
      await expect(photo).toHaveAccessibleDescription(
        new RegExp(`${escapeRegExp(photoHelpText)}[\\s\\S]*${escapeRegExp(profile.photoFirst)}`),
      );
      // Reading order inside the description: help before the file name.
      photoTokens = await describedByTokens(photo);
      const photoFileToken = photoTokens.find((token) => token.endsWith("-photo-file")) ?? "";
      expect(photoFileToken).not.toBe("");
      expect(photoTokens.indexOf(photoFileToken)).toBeGreaterThan(photoTokens.indexOf(`${photoFileToken.replace("-photo-file", "")}-photo-help`));
      // The logo's file id never leaks into the photo's list (cross-slot).
      expect(photoTokens.some((token) => token.endsWith("-logo-file"))).toBe(false);

      // Attach the logo, then replace the photo: only the new name survives.
      await logo.setInputFiles(pngFile(profile.logoFile));
      const logoHelpText = (await logoHelp.textContent()) ?? "";
      await expect(logo).toHaveAccessibleDescription(
        new RegExp(`${escapeRegExp(logoHelpText)}[\\s\\S]*${escapeRegExp(profile.logoFile)}`),
      );
      await photo.setInputFiles(pngFile(profile.photoSecond));
      await expect(photo).toHaveAccessibleDescription(
        new RegExp(`${escapeRegExp(photoHelpText)}[\\s\\S]*${escapeRegExp(profile.photoSecond)}`),
      );
      await expect(photo).not.toHaveAccessibleDescription(new RegExp(escapeRegExp(profile.photoFirst)));
      await expect(page.locator('small[id$="-photo-file"]')).toHaveCount(1); // replaced in place, not duplicated
      logoTokens = await describedByTokens(logo);
      expect(logoTokens.some((token) => token.endsWith("-photo-file"))).toBe(false);

      // Remove the logo by re-selecting nothing: row unmounts and its token
      // must leave the described-by list in the same frame's commit - a token
      // kept after the row dies is exactly the stale association #1165
      // forbids, and the photo row must stay described regardless.
      await logo.setInputFiles([]);
      await expect(page.locator('small[id$="-logo-file"]')).toHaveCount(0);
      logoTokens = await describedByTokens(logo);
      expect(logoTokens.some((token) => token.endsWith("-logo-file"))).toBe(false);
      await expect(logo).not.toHaveAccessibleDescription(new RegExp(escapeRegExp(profile.staleLogoName)));
      await expect(logo).toHaveAccessibleDescription(new RegExp(escapeRegExp(logoHelpText)));
      await expect(photo).toHaveAccessibleDescription(new RegExp(escapeRegExp(profile.photoSecond)));

      // 320px: the row stays visible, announced and overflow-free.
      await page.setViewportSize({ width: 320, height: 568 });
      await expect(page.locator('small[id$="-photo-file"]')).toBeVisible();
      await expect(photo).toHaveAccessibleDescription(new RegExp(escapeRegExp(profile.photoSecond)));
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, "no horizontal overflow at 320px").toBeLessThanOrEqual(1);

      // Absent-DOM-target sweep for every token on both controls, in this
      // final mixed state (photo attached, logo detached).
      for (const input of [photo, logo]) {
        for (const target of await resolveTargets(page, input)) {
          expect(target.exists, `described-by token "${target.token}" has a DOM target`).toBe(true);
          expect(target.text.trim(), `described-by target "${target.token}" speaks non-empty text`).not.toBe("");
        }
      }

      expect(unexpectedProviderRequests, "synthetic files never leave the browser").toEqual([]);
    });

    test(`a restored draft carries no stale file-name association (${profile.locale})`, async ({ authenticatedPage: page }) => {
      const { photo, logo, nameField } = await openWizard(page, profile.locale);

      // Persist a draft that recorded both asset names.
      await nameField.fill("Cedar & Silk");
      await photo.setInputFiles(pngFile(profile.photoFirst));
      await logo.setInputFiles(pngFile(profile.logoFile));
      await page.waitForFunction((expected) => {
        for (let index = 0; index < window.sessionStorage.length; index++) {
          const key = window.sessionStorage.key(index);
          if (key?.startsWith("taslim:draft:product-ad:") && window.sessionStorage.getItem(key)?.includes(expected)) return true;
        }
        return false;
      }, profile.photoFirst);

      // Reload: the names come back, the file bytes never do, so the wizard
      // must return to the unattached association shape.
      await page.reload();
      await expect(nameField).toHaveValue("Cedar & Silk");
      const restoredNotice = page.locator('p[id$="-draft-notice"]');
      await expect(restoredNotice).toBeVisible();

      for (const [slot, input] of [["photo", photo], ["logo", logo]] as const) {
        await expect(page.locator(`small[id$="-${slot}-file"]`)).toHaveCount(0);
        const tokens = await describedByTokens(input);
        expect(tokens.some((token) => token.endsWith(`-${slot}-file`)), `no stale ${slot} file token after restore`).toBe(false);
        for (const target of await resolveTargets(page, input)) {
          expect(target.exists, `restored state: token "${target.token}" has a DOM target`).toBe(true);
        }
        // The expired-assets retry instruction is what should describe the
        // control now (photoLogo kind covers both slots), not a dead row.
        expect(tokens.some((token) => token.endsWith("-draft-notice"))).toBe(true);
      }

      // Re-attaching the logo alone re-adds only the logo row and token.
      await logo.setInputFiles(pngFile(profile.photoSecond));
      await expect(page.locator('small[id$="-logo-file"]')).toHaveText(profile.photoSecond);
      const logoTokensAfter = await describedByTokens(logo);
      expect(logoTokensAfter.some((token) => token.endsWith("-logo-file"))).toBe(true);
      const photoTokensAfter = await describedByTokens(photo);
      expect(photoTokensAfter.some((token) => token.endsWith("-logo-file"))).toBe(false);
      expect(await resolveTargets(page, logo)).not.toEqual(
        expect.arrayContaining([expect.objectContaining({ exists: false })]),
      );
    });
  }
});
