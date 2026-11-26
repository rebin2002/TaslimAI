import { test, expect } from "./fixtures";

// Browser-level acceptance for the transient masked first paint of the
// authenticated /create/product-ad route (PR #1022). The wizard is never part
// of the static server HTML: ProtectedPage renders nothing until the client
// session probe resolves, so the masked commit the fix targets is the first
// real-browser paint after the persisted RTL locale is applied. It exists only
// between that commit and the queued sessionStorage restore, which a locator
// cannot reliably query - so an init-script MutationObserver records every
// masked role="status" node with the lang/dir of the section that carried it,
// and the assertions run on that transcript afterwards.
const onePixelPng = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aGQAAAAASUVORK5CYII=",
  "base64",
);

const englishMaskLiteral = "Restoring saved product-ad draft";
const englishArtworkLiteral = "Local product-photo preview";

const profiles = [
  {
    locale: "ar",
    lang: "ar",
    dir: "rtl",
    mask: "جارٍ استعادة مسودة الإعلان…",
    artworkLabel: "معاينة محلية لصورة المنتج",
  },
  {
    locale: "ku",
    lang: "ckb",
    dir: "rtl",
    mask: "ڕەشنووسی ڕیکلامی بەرهەم ڕێک دەکرێتەوە…",
    artworkLabel: "پێشبینینی ناوخۆیی وێنەی بەرهەم",
  },
] as const;

type MaskSample = { text: string; lang: string; dir: string };

for (const profile of profiles) {
  test(`masked RTL first paint and preview artwork name are localized (${profile.locale})`, async ({ authenticatedPage: page }) => {
    const unexpectedProviderRequests: string[] = [];
    page.on("request", (request) => {
      if (/sunoapi|runway|\/api\/(generation|payments|checkout|provider)/i.test(request.url())) {
        unexpectedProviderRequests.push(request.url());
      }
    });

    // Runs at document start on this context's later navigations: seeds the
    // device locale exactly like the settings selector would, then transcripts
    // every wizard-mask status node that is ever committed. The mask paragraph
    // is the only role="status" in the wizard without aria-live, and only the
    // wizard shell section carries a lang attribute, so the transcript cannot
    // pick up the draft-notice or the preview announcer.
    await page.addInitScript((seed) => {
      window.localStorage.setItem("taslim-locale", seed);
      const w = window as unknown as { __productAdMaskSamples?: MaskSample[] };
      if (!w.__productAdMaskSamples) {
        w.__productAdMaskSamples = [];
        const record = (node: Node) => {
          if (!(node instanceof HTMLElement)) return;
          const found = node.matches('p[role="status"]:not([aria-live])')
            ? [node]
            : [];
          found.push(...(Array.from(node.querySelectorAll?.('p[role="status"]:not([aria-live])') ?? []) as HTMLElement[]));
          for (const el of found) {
            const section = el.closest("section[lang]");
            if (!section) return;
            w.__productAdMaskSamples!.push({
              text: el.textContent ?? "",
              lang: section.getAttribute("lang") ?? "",
              dir: section.getAttribute("dir") ?? "",
            });
          }
        };
        new MutationObserver((mutations) => {
          for (const mutation of mutations) mutation.addedNodes.forEach(record);
        }).observe(document, { childList: true, subtree: true });
      }
    }, profile.locale);

    await page.goto("/create/product-ad");

    // Stable state proves the session really reached the authenticated wizard:
    // the unmasked form is up, still in the persisted RTL locale.
    const name = page.locator('input[id$="-product-name"]');
    await expect(name).toBeVisible();
    const shell = page.locator("section[lang]");
    await expect(shell).toHaveAttribute("lang", profile.lang);
    await expect(shell).toHaveAttribute("dir", profile.dir);

    const samples = await page.evaluate(() => (window as unknown as { __productAdMaskSamples?: MaskSample[] }).__productAdMaskSamples ?? []);
    const maskedFirstPaints = samples.filter((sample) => sample.dir === profile.dir);
    // The regression this pins cannot fire without an RTL masked commit, so a
    // silent transcript is an acceptance failure, not a pass.
    expect(maskedFirstPaints.length, `at least one masked ${profile.dir} first paint was committed`).toBeGreaterThan(0);
    for (const sample of maskedFirstPaints) {
      expect(sample.lang, "masked commit carries the persisted locale tag").toBe(profile.lang);
      expect(sample.text, "a live RTL status region must not speak English").not.toContain(englishMaskLiteral);
    }
    expect(
      maskedFirstPaints.some((sample) => sample.text === profile.mask),
      `masked status region announces "${profile.mask}" in ${profile.locale}`,
    ).toBe(true);

    // The preview artwork name is stable DOM, asserted directly: a local-only
    // preview build (no upload, no provider), exactly like the draft suite.
    await name.fill("Cedar & Silk");
    await page.locator('input[id$="-product-photo"]').setInputFiles({
      name: "product.png",
      mimeType: "image/png",
      buffer: onePixelPng,
    });
    await page.locator('form button[type="submit"]').click();
    const artwork = page.locator("aside div[aria-label]", {
      has: page.locator('img[data-preview-asset="product"]'),
    });
    await expect(artwork).toHaveAttribute("aria-label", profile.artworkLabel);
    await expect(artwork).not.toHaveAttribute("aria-label", englishArtworkLiteral);

    expect(unexpectedProviderRequests, "no paid provider is contacted").toEqual([]);
  });
}
