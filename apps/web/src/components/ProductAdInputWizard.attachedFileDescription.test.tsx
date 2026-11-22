import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { localeTag, locales } from "@/lib/i18n";
import { ProductAdInputWizard, productAdFileDescriptionIds } from "./ProductAdInputWizard";

/**
 * The wizard paints the chosen file name under each upload control
 * (`ProductAdInputWizard.tsx`, the `.fileName` rows) as the only on-screen proof
 * of *what* is attached. That row sat outside the accessibility tree: it had no
 * id, and `productAdFileDescriptionIds` - the helper this component already uses
 * to bind every other fact to its control - never named it. So a screen reader
 * user who picked a brand logo heard "Brand logo (optional)" plus the help
 * sentence and nothing else: the state #785 pins (the logo survives a later photo
 * replacement) is invisible to them, and #798's removal is indistinguishable from
 * never having attached one.
 *
 * This lane is the presentation-only half of that pair: the row becomes a real
 * relationship through `aria-describedby`, with no state machine, no live-region
 * announcement, no provider call and no upload.
 *
 * Deterministic and dependency-free, matching the convention of the sibling
 * suites (#757, #937, #1022): a pure-function contract over the helper, a source
 * guard that the shipped JSX uses exactly the ids the helper emits, and static
 * markup of the tree the route commits. No jsdom or Testing Library is installed
 * in `apps/web`, and none was added.
 */

const wizardSource = readFileSync(
  fileURLToPath(new URL("./ProductAdInputWizard.tsx", import.meta.url)),
  "utf8",
);

/** The flags the component actually passes, minus the per-slot file state. */
type Flags = Parameters<typeof productAdFileDescriptionIds>[2];
const noNotice: Flags = { noticeKind: "none", error: false };

function renderedIds(html: string): Set<string> {
  return new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((match) => match[1]));
}

function describedBy(html: string): string[] {
  return [...html.matchAll(/aria-describedby="([^"]+)"/g)].flatMap((match) => match[1].split(" "));
}

/** The `<small>` row that names the attached file for one slot. */
function fileNameRow(field: "photo" | "logo"): string {
  const state = field === "photo" ? "productPhoto" : "brandLogo";
  const row = wizardSource.match(new RegExp(`\\{${state} && <small[^>]*className=\\{styles\\.fileName\\}[^>]*>`));
  if (!row) throw new Error(`no rendered .fileName row for ${field}`);
  return row[0];
}

describe("Product Ad wizard attached-file descriptions", () => {
  it("names the attached-file row only while that slot holds a file", () => {
    expect(productAdFileDescriptionIds("wizard", "photo", { ...noNotice, attached: true }))
      .toBe("wizard-photo-help wizard-photo-file");
    expect(productAdFileDescriptionIds("wizard", "logo", { ...noNotice, attached: true }))
      .toBe("wizard-logo-help wizard-logo-file");
    // The default stays the help sentence alone, so an empty control promises nothing.
    expect(productAdFileDescriptionIds("wizard", "photo", noNotice)).toBe("wizard-photo-help");
    expect(productAdFileDescriptionIds("wizard", "photo")).toBe("wizard-photo-help");
    expect(productAdFileDescriptionIds("wizard", "photo", { ...noNotice, attached: false }))
      .not.toContain("file");
  });

  it("binds each slot's own row and never the other slot's", () => {
    const photo = productAdFileDescriptionIds("wizard", "photo", { ...noNotice, attached: true });
    const logo = productAdFileDescriptionIds("wizard", "logo", { ...noNotice, attached: true });
    expect(photo).not.toContain("logo");
    expect(logo).not.toContain("photo");
    // A live photo plus a remembered-but-expired logo still describes the photo.
    expect(productAdFileDescriptionIds("wizard", "photo", { noticeKind: "photoLogo", error: false, attached: true }))
      .toBe("wizard-photo-help wizard-photo-file wizard-draft-notice");
    // The logo is optional, so an expired logo is disclosed on the logo control only.
    expect(productAdFileDescriptionIds("wizard", "logo", { noticeKind: "photoLogo", error: false, attached: false }))
      .toBe("wizard-logo-help wizard-draft-notice");
  });

  it("reads the selected file before the retry instruction and the rejection", () => {
    const both = productAdFileDescriptionIds("wizard", "photo", { noticeKind: "photoLogo", error: true, attached: true });
    expect(both.split(" ")).toEqual([
      "wizard-photo-help",
      "wizard-photo-file",
      "wizard-draft-notice",
      "wizard-photo-error",
    ]);
  });

  it("emits the exact id the shipped JSX puts on the file-name row", () => {
    for (const field of ["photo", "logo"] as const) {
      const suffix = productAdFileDescriptionIds("wizard", field, { ...noNotice, attached: true })
        .split(" ")
        .find((token) => token.endsWith("-file"));
      expect(suffix, `${field} helper id`).toBe(`wizard-${field}-file`);
      expect(wizardSource, `${field} row carries the helper id`)
        .toContain("id={`${id}-" + field + "-file`}");
    }
  });

  it("drives each control's description from that control's own file state", () => {
    expect(wizardSource).toContain(
      'productAdFileDescriptionIds(id, "photo", { noticeKind: pendingAssetKind, error: photoError.length > 0, attached: productPhoto !== null })',
    );
    expect(wizardSource).toContain(
      'productAdFileDescriptionIds(id, "logo", { noticeKind: pendingAssetKind, error: logoError.length > 0, attached: brandLogo !== null })',
    );
  });

  it("keeps the file name direction-aware for Arabic and Sorani file names", () => {
    for (const field of ["photo", "logo"] as const) {
      expect(fileNameRow(field)).toContain('dir="auto"');
    }
  });

  it("stays a description rather than becoming an unrequested announcement", () => {
    // No live region, role or state writer is introduced for the name row: this
    // lane adds a relationship, not a status message.
    expect(fileNameRow("photo")).not.toMatch(/role=|aria-live/);
    expect(fileNameRow("logo")).not.toMatch(/role=|aria-live/);
    expect(wizardSource).not.toMatch(/fileName[^>]*aria-live/);
    expect(wizardSource.match(/<small className=\{styles\.fileName\}/g)).toBeNull();
    expect(wizardSource.match(/className=\{styles\.fileName\}/g)).toHaveLength(2);
  });

  it("renders nothing dangling in the mounted-only states, in every locale", () => {
    for (const locale of locales) {
      const html = renderToStaticMarkup(<ProductAdInputWizard locale={locale} />);
      expect(html).toContain(`lang="${localeTag(locale)}"`);
      const ids = renderedIds(html);
      // Nothing is attached on the first commit, so no name row exists...
      expect([...ids].some((value) => value.endsWith("-file"))).toBe(false);
      // ...and nothing references one either.
      expect(describedBy(html).filter((token) => token.endsWith("-file"))).toEqual([]);
      const dangling = describedBy(html).filter((token) => !ids.has(token));
      expect(dangling, `${locale} dangling described-by: ${dangling.join(", ")}`).toEqual([]);
    }
  });

  it("leaves the required photo control required and the optional logo control optional", () => {
    const html = renderToStaticMarkup(<ProductAdInputWizard />);
    const [photoInput, logoInput] = [...html.matchAll(/<input id="[^"]+" class="[^"]*fileInput[^"]*"[^>]*>/g)].map((m) => m[0]);
    expect(photoInput).toContain('required=""');
    expect(logoInput).not.toMatch(/required=|aria-required=/);
    expect(photoInput).toContain('aria-describedby="');
    expect(logoInput).toContain('aria-describedby="');
  });
});
