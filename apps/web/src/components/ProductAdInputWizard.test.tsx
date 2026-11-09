import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import {
  buildProductAdMockPreview,
  defaultProductAdOutput,
  isSupportedProductAdImage,
  MAX_PRODUCT_AD_IMAGE_BYTES,
  ProductAdInputWizard,
} from "./ProductAdInputWizard";
import { localeDirection, localeTag, locales } from "@/lib/i18n";

describe("ProductAdInputWizard", () => {
  it("defaults to an Instagram square post and requires a local product photo but not a logo", () => {
    const html = renderToStaticMarkup(<ProductAdInputWizard />);

    expect(defaultProductAdOutput).toEqual({ channel: "instagram", format: "square" });
    expect(html).toContain("Product advertisement");
    expect(html).toContain('type="text"');
    expect(html.match(/type="file" accept="image\/\*"/g)).toHaveLength(2);
    expect(html).toMatch(/type="file"[^>]*required=""/);
    expect(html.match(/required=""/g)).toHaveLength(2);
    expect(html).toContain("Product photo (required)");
    expect(html).toContain("Brand logo (optional)");
    expect(html).toContain('value="instagram" selected=""');
    expect(html).toContain('value="square" selected=""');
    expect(html).toContain('aria-labelledby=');
    expect(html).toContain("Local-only preview");
  });

  it("renders Arabic and Sorani copy in RTL with their correct language tags", () => {
    for (const locale of ["ar", "ku"] as const) {
      const html = renderToStaticMarkup(<ProductAdInputWizard locale={locale} />);
      expect(html).toContain(`lang="${localeTag(locale)}"`);
      expect(html).toContain(`dir="${localeDirection(locale)}"`);
      expect(html).toContain('dir="auto"');
      expect(html).toContain(locale === "ar" ? "ابدأ بمنتجك." : "لە بەرهەمەکەتەوە دەست پێ بکە.");
      expect(html).toContain(locale === "ar" ? "شعار العلامة التجارية (اختياري)" : "لۆگۆی براند (ئارەزوومەندانە)");
    }
    expect(localeTag("ku")).toBe("ckb");
    expect(locales).toContain("ku");
  });

  it("builds a deterministic local draft from minimal product input and selected output settings", () => {
    const input = {
      productName: "  Cedar & Silk  ",
      benefit: "  Calm, soft skin  ",
      channel: "tiktok" as const,
      format: "story" as const,
    };
    const expected = {
      headline: "Cedar & Silk",
      caption: "Calm, soft skin — discover Cedar & Silk today.",
      channel: "TikTok",
      format: "Story · 9:16",
    };

    expect(buildProductAdMockPreview(input, "en")).toEqual(expected);
    expect(buildProductAdMockPreview(input, "en")).toEqual(expected);
  });

  it("uses the selected locale for mock copy and accepts only bounded image files", () => {
    const input = { productName: "صابون", benefit: "طبيعي", channel: "instagram" as const, format: "square" as const };
    const validImage = { type: "image/png", size: MAX_PRODUCT_AD_IMAGE_BYTES };

    expect(buildProductAdMockPreview(input, "ar").caption).toContain("اكتشف صابون اليوم");
    expect(isSupportedProductAdImage(validImage)).toBe(true);
    expect(isSupportedProductAdImage({ type: "image/png", size: MAX_PRODUCT_AD_IMAGE_BYTES + 1 })).toBe(false);
    expect(isSupportedProductAdImage({ type: "text/plain", size: 128 })).toBe(false);
  });

  it("is explicit that the wizard only makes a local mock and does not expose provider endpoints", () => {
    const html = renderToStaticMarkup(<ProductAdInputWizard locale="en" />);
    expect(html).toContain("nothing is uploaded or sent to an AI provider");
    expect(html).toContain("No files are uploaded, no provider is called, and nothing is published.");
    expect(html).not.toContain("/api/");
    expect(html).not.toContain("fetch(");
  });
});
