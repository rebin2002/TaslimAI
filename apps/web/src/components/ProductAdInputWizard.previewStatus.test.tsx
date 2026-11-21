import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { localeTag, locales } from "@/lib/i18n";
import {
  buildProductAdPreviewAnnouncement,
  defaultProductAdOutput,
  nextProductAdPreviewStatus,
  ProductAdInputWizard,
} from "./ProductAdInputWizard";

const source = readFileSync(fileURLToPath(new URL("./ProductAdInputWizard.tsx", import.meta.url)), "utf8");

describe("Product Ad integrated preview announcements", () => {
  it("mounts an initially silent status region before any preview is generated", () => {
    const html = renderToStaticMarkup(<ProductAdInputWizard />);
    expect(html).toContain("Your draft preview will appear here");
    expect(html).toMatch(/<p class="sr-only" role="status" aria-live="polite" aria-atomic="true"><\/p>/);
    expect(html).toContain('aria-live="polite"');
    // A conditional preview card cannot serve as a reliable live region.
    expect(source).not.toMatch(/previewCard[^>]*aria-live/);
    expect(source).toContain('buildProductAdPreviewAnnouncement(previewStatus, locale');
  });

  it("keeps the live region under localized and RTL-aware wizard markup", () => {
    for (const locale of locales) {
      const html = renderToStaticMarkup(<ProductAdInputWizard locale={locale} />);
      expect(html).toContain(`lang="${localeTag(locale)}"`);
      expect(html).toContain('role="status"');
      expect(html).toContain('aria-atomic="true"');
    }
  });

  it("does not falsely announce a preview from initial edits and announces clearing once", () => {
    expect(nextProductAdPreviewStatus("idle", "edited")).toBe("idle");
    expect(nextProductAdPreviewStatus("idle", "generated")).toBe("ready");
    expect(nextProductAdPreviewStatus("ready", "edited")).toBe("cleared");
    expect(nextProductAdPreviewStatus("cleared", "edited")).toBe("cleared");
    expect(nextProductAdPreviewStatus("cleared", "generated")).toBe("ready");
  });

  it("announces actual selected channel/format and the safe defaults", () => {
    expect(defaultProductAdOutput).toEqual({ channel: "instagram", format: "square" });
    expect(buildProductAdPreviewAnnouncement("ready", "en")).toContain("Instagram");
    expect(buildProductAdPreviewAnnouncement("ready", "en")).toContain("Feed post · 1:1");
    const actual = buildProductAdPreviewAnnouncement("ready", "en", { channel: "tiktok", format: "story" });
    expect(actual).toContain("TikTok");
    expect(actual).toContain("Story · 9:16");
    expect(actual).not.toContain("Instagram");
  });

  it("is silent for idle, distinct for ready/cleared, and free of network/provider claims", () => {
    for (const locale of locales) {
      expect(buildProductAdPreviewAnnouncement("idle", locale)).toBe("");
      const ready = buildProductAdPreviewAnnouncement("ready", locale);
      const cleared = buildProductAdPreviewAnnouncement("cleared", locale);
      expect(ready.length).toBeGreaterThan(0);
      expect(cleared.length).toBeGreaterThan(0);
      expect(ready).not.toBe(cleared);
      expect(ready + cleared).not.toMatch(/https?:\/\/|\/api\//);
    }
  });

  it("retains restoration reset semantics on account/workspace change", () => {
    expect(source).toContain('setPreviewStatus("idle");');
    expect(source).toContain("setHydratedKey(key);");
    expect(source).toContain("invalidatePreview();");
  });
});
