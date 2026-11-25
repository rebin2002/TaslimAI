import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { locales } from "@/lib/i18n";
import {
  blankProductAdDraft,
  buildProductAdDraftRestoredNotice,
  productAdPendingAssetNoticeKind,
  type ProductAdDraftState,
} from "@/lib/productAdDraftState";
import { ProductAdInputWizard, productAdFileDescriptionIds } from "./ProductAdInputWizard";

const wizardSource = readFileSync(fileURLToPath(new URL("./ProductAdInputWizard.tsx", import.meta.url)), "utf8");

/** A draft as it comes back from session storage: names survive, image bytes do not. */
function expiredUploadDraft(overrides: Partial<ProductAdDraftState> = {}): ProductAdDraftState {
  return {
    ...blankProductAdDraft(1),
    version: 1,
    productName: "Cedar & Silk hand cream",
    benefit: "Calm, soft skin",
    productPhoto: { name: "photo.jpg", mime: "image/jpeg", bytes: 1024 },
    brandLogo: null,
    ...overrides,
  };
}

/** Collect every id the rendered document actually owns, to catch dangling references. */
function renderedIds(html: string): Set<string> {
  return new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((match) => match[1]));
}

describe("ProductAdInputWizard expired upload retry announcements", () => {
  it("mounts the draft notice region empty so the expired-upload instruction can be announced later", () => {
    const html = renderToStaticMarkup(<ProductAdInputWizard />);
    // Present, live, and silent on the first commit: an aria-live region that is
    // inserted together with its text is not announced by assistive technology.
    expect(html).toMatch(/<p id="[^"]*-draft-notice" class="[^"]*draftNotice[^"]*" role="status" aria-live="polite" aria-atomic="true"><\/p>/);
    // Nothing to claim yet, because no draft has been restored.
    expect(html).not.toContain("Draft restored");
    expect(html).not.toContain("Re-attach");
  });

  it("never re-introduces the conditional live region that swallowed the instruction", () => {
    expect(wizardSource).not.toMatch(/restoredDraft && <p[^>]*aria-live/);
    expect(wizardSource).toContain('id={`${id}-draft-notice`}');
    expect(wizardSource).toContain('aria-live="polite" aria-atomic="true">{draftNotice}');
  });

  it("treats an image as expired until the user picks a live file for that slot", () => {
    const draft = expiredUploadDraft();
    expect(productAdPendingAssetNoticeKind(draft)).toBe("photoOnly");
    expect(productAdPendingAssetNoticeKind(draft, { productPhoto: false, brandLogo: false })).toBe("photoOnly");
    // Retrying the upload retires the instruction even though the stored
    // descriptor is still present in the restored draft.
    expect(productAdPendingAssetNoticeKind(draft, { productPhoto: true, brandLogo: false })).toBe("none");
    const both = expiredUploadDraft({ brandLogo: { name: "logo.png", mime: "image/png", bytes: 512 } });
    expect(productAdPendingAssetNoticeKind(both)).toBe("photoLogo");
    expect(productAdPendingAssetNoticeKind(both, { productPhoto: true, brandLogo: false })).toBe("none");
    // The brand logo is optional: an expired logo never blocks the required retry.
    expect(productAdPendingAssetNoticeKind(expiredUploadDraft({ productPhoto: null, brandLogo: { name: "logo.png", mime: "image/png", bytes: 512 } }))).toBe("none");
    expect(productAdPendingAssetNoticeKind(null)).toBe("none");
    expect(productAdPendingAssetNoticeKind(undefined)).toBe("none");
  });

  it("keeps the restored-draft sentence truthful across the retry", () => {
    const draft = expiredUploadDraft();
    expect(buildProductAdDraftRestoredNotice(draft, "en")).toContain("Re-attach the product photo to continue.");
    const afterRetry = buildProductAdDraftRestoredNotice(draft, "en", { productPhoto: true, brandLogo: false });
    expect(afterRetry).toBe("Draft restored: Cedar & Silk hand cream.");
    expect(afterRetry).not.toMatch(/re-attach/i);
    // Before the retry the greeting and the instruction arrive together.
    expect(buildProductAdDraftRestoredNotice(draft, "en")).toContain("Draft restored: Cedar & Silk hand cream.");
  });

  it("still instructs a re-attach when the expired draft carries no product name", () => {
    // A photo-only draft is storable but nameless, and the required upload is
    // empty after the reload: the instruction is the only useful announcement.
    const nameless = expiredUploadDraft({ productName: "   " });
    expect(buildProductAdDraftRestoredNotice(nameless, "en")).toBe("Re-attach the product photo to continue.");
    expect(buildProductAdDraftRestoredNotice(nameless, "en", { productPhoto: true, brandLogo: false })).toBe("");
    expect(buildProductAdDraftRestoredNotice(null, "en")).toBe("");
    expect(buildProductAdDraftRestoredNotice(undefined, "en")).toBe("");
    expect(buildProductAdDraftRestoredNotice(blankProductAdDraft(1), "en")).toBe("");
  });

  it("states the expired-upload retry in every wizard locale without network claims", () => {
    const draft = expiredUploadDraft({ brandLogo: { name: "logo.png", mime: "image/png", bytes: 512 } });
    for (const locale of locales) {
      const pending = buildProductAdDraftRestoredNotice(draft, locale);
      expect(pending.length).toBeGreaterThan(0);
      expect(pending).not.toMatch(/https?:\/\/|\/api\/|uploaded|provider/i);
      // Retrying both uploads retires the instruction in every locale.
      expect(buildProductAdDraftRestoredNotice(draft, locale, { productPhoto: true, brandLogo: true })).not.toMatch(
        /Re-attach|أعد إرفاق|دووبارە لەگەڵدا/,
      );
    }
    expect(buildProductAdDraftRestoredNotice(draft, "ar")).toContain("أعد إرفاق صورة المنتج وشعار العلامة التجارية");
    expect(buildProductAdDraftRestoredNotice(draft, "ku")).toContain("دووبارە لەگەڵدا بخە");
  });

  it("binds only rendered descriptions to each upload control", () => {
    const base = "wizard";
    expect(productAdFileDescriptionIds(base, "photo")).toBe("wizard-photo-help");
    expect(productAdFileDescriptionIds(base, "logo")).toBe("wizard-logo-help");
    // The expired-upload retry belongs to the required photo control, and to the
    // optional logo control only when that file is expired as well.
    expect(productAdFileDescriptionIds(base, "photo", { noticeKind: "photoOnly", error: false })).toBe("wizard-photo-help wizard-draft-notice");
    expect(productAdFileDescriptionIds(base, "logo", { noticeKind: "photoOnly", error: false })).toBe("wizard-logo-help");
    expect(productAdFileDescriptionIds(base, "logo", { noticeKind: "photoLogo", error: false })).toBe("wizard-logo-help wizard-draft-notice");
    // A rejected file explains itself on the control that carries aria-invalid.
    expect(productAdFileDescriptionIds(base, "photo", { noticeKind: "none", error: true })).toBe("wizard-photo-help wizard-photo-error");
    expect(productAdFileDescriptionIds(base, "photo", { noticeKind: "photoLogo", error: true })).toBe("wizard-photo-help wizard-draft-notice wizard-photo-error");
    expect(productAdFileDescriptionIds(base, "logo", { noticeKind: "photoLogo", error: true })).toBe("wizard-logo-help wizard-draft-notice wizard-logo-error");
    // Nothing is ever referenced before it exists.
    expect(productAdFileDescriptionIds(base, "photo", { noticeKind: "none", error: false })).not.toContain("error");
    expect(productAdFileDescriptionIds(base, "photo", { noticeKind: "none", error: false })).not.toContain("draft-notice");
  });

  it("resolves every upload described-by id against the rendered document", () => {
    const html = renderToStaticMarkup(<ProductAdInputWizard />);
    const ids = renderedIds(html);
    const photo = html.match(/<input id="([^"]+)"[^>]*aria-describedby="([^"]+)"/);
    const describedBy = [...html.matchAll(/aria-describedby="([^"]+)"/g)].flatMap((match) => match[1].split(" "));
    expect(describedBy.length).toBeGreaterThan(0);
    const dangling = describedBy.filter((referenced) => !ids.has(referenced));
    expect(dangling, `described-by ids with no element: ${dangling.join(", ")}`).toEqual([]);
    // The always-mounted notice region is itself addressable from the photo control.
    expect([...ids].some((id) => id.endsWith("-draft-notice"))).toBe(true);
    expect(photo).not.toBeNull();
  });

  it("keeps the required upload marked invalid and self-explanatory for all locales", () => {
    for (const locale of locales) {
      const html = renderToStaticMarkup(<ProductAdInputWizard locale={locale} />);
      const ids = renderedIds(html);
      const fileInputs = [...html.matchAll(/<input id="[^"]+" class="[^"]*fileInput[^"]*"[^>]*>/g)];
      expect(fileInputs).toHaveLength(2);
      for (const [markup] of fileInputs) {
        const inputId = markup.match(/\bid="([^"]+)"/)![1];
        expect(ids.has(inputId)).toBe(true);
        const described = markup.match(/aria-describedby="([^"]+)"/)?.[1];
        expect(described, `file input ${inputId} has no accessible description`).toBeTruthy();
        for (const referenced of (described ?? "").split(" ")) {
          expect(ids.has(referenced), `dangling description ${referenced}`).toBe(true);
        }
      }
    }
  });
});
