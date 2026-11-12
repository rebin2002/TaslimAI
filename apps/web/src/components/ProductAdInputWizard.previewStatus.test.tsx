import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import {
  buildProductAdPreviewAnnouncement,
  defaultProductAdOutput,
  nextProductAdPreviewStatus,
  ProductAdInputWizard,
} from "./ProductAdInputWizard";
import { localeTag, locales } from "@/lib/i18n";

const componentSource = readFileSync(fileURLToPath(new URL("./ProductAdInputWizard.tsx", import.meta.url)), "utf8");

describe("ProductAdInputWizard preview status region", () => {
  it("mounts a polite status live region before any preview exists", () => {
    const html = renderToStaticMarkup(<ProductAdInputWizard />);

    // The region has to be in the accessibility tree while the draft preview is
    // still empty; a region inserted together with its content is not announced.
    expect(html).toContain("Your draft preview will appear here");
    expect(html).toContain('aria-live="polite"');
    expect(html).toContain('role="status"');
    expect(html).toContain('aria-atomic="true"');
    expect(html).toContain('class="sr-only"');
    // Nothing is announced on mount, so the first keystroke cannot read out a
    // preview that was never created.
    expect(html).toMatch(/<p class="sr-only" role="status" aria-live="polite" aria-atomic="true"><\/p>/);
    // Exactly one live region: the persistent one, not a per-render duplicate.
    expect(html.match(/aria-live="polite"/g)).toHaveLength(1);
  });

  it("keeps the status region outside the conditionally rendered preview card", () => {
    // Regression guard for WCAG 4.1.3: the card only exists once a preview has
    // been built, so it must never be the element carrying aria-live.
    expect(componentSource).not.toMatch(/previewCard[^>]*aria-live/);
    expect(componentSource.match(/aria-live="polite"/g)).toHaveLength(1);
  });

  it("reports the status region with the locale tag and direction of the wizard", () => {
    for (const locale of locales) {
      const html = renderToStaticMarkup(<ProductAdInputWizard locale={locale} />);
      expect(html).toContain(`lang="${localeTag(locale)}"`);
      expect(html).toContain('aria-live="polite"');
    }
  });

  it("stays silent until a preview is generated, then clears only once", () => {
    expect(nextProductAdPreviewStatus("idle", "edited")).toBe("idle");
    expect(nextProductAdPreviewStatus("idle", "generated")).toBe("ready");
    expect(nextProductAdPreviewStatus("ready", "edited")).toBe("cleared");
    // Repeated edits must not re-announce the same cleared state.
    expect(nextProductAdPreviewStatus("cleared", "edited")).toBe("cleared");
    expect(nextProductAdPreviewStatus("cleared", "generated")).toBe("ready");
  });

  it("announces the selected safe defaults for a newly built preview", () => {
    expect(defaultProductAdOutput).toEqual({ channel: "instagram", format: "square" });
    expect(buildProductAdPreviewAnnouncement("ready", "en")).toContain("Instagram");
    expect(buildProductAdPreviewAnnouncement("ready", "en")).toContain("Feed post · 1:1");
  });

  it("announces the chosen output rather than the defaults", () => {
    const announcement = buildProductAdPreviewAnnouncement("ready", "en", { channel: "tiktok", format: "story" });
    expect(announcement).toContain("TikTok");
    expect(announcement).toContain("Story · 9:16");
    expect(announcement).not.toContain("Instagram");
  });

  it("announces idle as nothing and every other status in all supported locales", () => {
    for (const locale of locales) {
      expect(buildProductAdPreviewAnnouncement("idle", locale)).toBe("");
      const ready = buildProductAdPreviewAnnouncement("ready", locale);
      const cleared = buildProductAdPreviewAnnouncement("cleared", locale);
      expect(ready.length).toBeGreaterThan(0);
      expect(cleared.length).toBeGreaterThan(0);
      // The two states must be distinguishable when read out.
      expect(ready).not.toBe(cleared);
    }
  });

  it("keeps status announcements local-only", () => {
    for (const locale of locales) {
      for (const status of ["ready", "cleared"] as const) {
        const announcement = buildProductAdPreviewAnnouncement(status, locale);
        expect(announcement).not.toContain("/api/");
        expect(announcement).not.toMatch(/https?:\/\//);
      }
    }
  });
});
