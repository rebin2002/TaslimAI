import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import {
  buildProductAdMockPreview,
  isSupportedProductAdImage,
  MAX_PRODUCT_AD_IMAGE_BYTES,
  ProductAdInputWizard,
  type ProductAdDraftInput,
  type ProductAdMockPreview,
} from "./ProductAdInputWizard";
import { locales } from "@/lib/i18n";

/**
 * Covers the add-then-remove logo journey on the product ad wizard: the user
 * attaches an authorized (accepted) logo, takes it back off again, and then
 * continues to the draft. The invariant is that nothing from the discarded
 * logo survives into what the wizard submits, and nothing is uploaded or sent
 * to a provider anywhere along the way.
 *
 * `apps/web` has no jsdom or Testing Library installed and this test does not
 * add either, so it follows the convention already used by the sibling wizard
 * suites: assertions run against the shipped handler source, extracted with a
 * brace matcher rather than a copy of the logic, while the submitted payload is
 * inspected from the real exported builder at runtime.
 */

const componentSource = readFileSync(
  fileURLToPath(new URL("./ProductAdInputWizard.tsx", import.meta.url)),
  "utf8",
);

/** Extract a `function name(...) { ... }` body from the real component source. */
function functionBody(name: string): string {
  const signature = new RegExp(`function ${name}\\([^)]*\\)\\s*\\{`);
  const match = signature.exec(componentSource);
  if (!match) throw new Error(`missing function ${name}() in ProductAdInputWizard.tsx`);
  let depth = 0;
  for (let index = match.index + match[0].length - 1; index < componentSource.length; index += 1) {
    if (componentSource[index] === "{") depth += 1;
    else if (componentSource[index] === "}") {
      depth -= 1;
      if (depth === 0) return componentSource.slice(match.index, index + 1);
    }
  }
  throw new Error(`unterminated function ${name}() in ProductAdInputWizard.tsx`);
}

/** The JSX element that renders the logo row of the generated preview card. */
function previewLogoRow(): string {
  const start = componentSource.indexOf("{brandLogo &&", componentSource.indexOf("previewCard"));
  const end = componentSource.indexOf("</small>", start);
  if (start === -1 || end === -1) throw new Error("missing preview-card logo row in ProductAdInputWizard.tsx");
  return componentSource.slice(start, end + "</small>".length);
}

/** Mock assets only: plain descriptors, never a real `File`, blob, or upload. */
function stubImage(name: string, size = 1024, type = "image/png") {
  return { name, type, size };
}

const acceptedLogo = stubImage("mock-brand-logo.png");
const acceptedPhoto = stubImage("mock-product-photo.jpg");
const unauthorizedLogo = stubImage("mock-brand-logo.pdf", 1024, "application/pdf");
const oversizedLogo = stubImage("mock-oversized-logo.png", MAX_PRODUCT_AD_IMAGE_BYTES + 1);

/**
 * The draft the wizard submits after the logo was attached and then removed.
 * Text and output settings only, exactly as the form holds them.
 */
const draftAfterLogoRemoved: ProductAdDraftInput = {
  productName: "Cedar & Silk",
  benefit: "Calm, soft skin",
  channel: "instagram",
  format: "square",
};

/**
 * Compile-time guard: the draft contract is keyed to these fields and no other.
 * Adding an image or asset field to `ProductAdDraftInput` (which is how a stale
 * logo reference would reach the submitted draft in the first place) makes this
 * object literal fail `tsc --noEmit` instead of passing silently.
 */
const draftFields: Record<keyof ProductAdDraftInput, string> = {
  productName: "productName",
  benefit: "benefit",
  channel: "channel",
  format: "format",
};

/** Same guard for the returned preview: four text fields, no asset slot. */
const previewFields: Record<keyof ProductAdMockPreview, string> = {
  headline: "headline",
  caption: "caption",
  channel: "channel",
  format: "format",
};

describe("ProductAdInputWizard logo removal and stale reference suppression", () => {
  it("treats an emptied logo selection as a clear instead of ignoring it", () => {
    const logoBody = functionBody("updateBrandLogo");

    // The selection is read null-safely, so "no file chosen anymore" is a value
    // the handler sees rather than an absent event it never notices.
    expect(logoBody).toMatch(/event\.currentTarget\.files\?\.\[0\]\s*\?\?\s*null/);

    // A removal must not short-circuit: bailing out on an empty selection would
    // leave the previously attached logo attached.
    expect(logoBody).not.toMatch(/if\s*\(\s*!file\s*\)\s*return/);
    expect(logoBody).not.toMatch(/if\s*\(\s*file\s*===\s*null\s*\)\s*return/);
    expect(logoBody).not.toMatch(/if\s*\(\s*file\s*\)\s*return/);

    // Only a rejected *file* takes the early return, so `null` always falls
    // through to the assignment below, which is fed the nullable selection.
    expect(logoBody).toMatch(/if\s*\(\s*file\s*&&\s*!isSupportedProductAdImage\(file\)\s*\)/);
    // The assignment has to run unconditionally at the end of the handler. A
    // truthiness guard around it (`if (file) setBrandLogo(file)`) would accept
    // an authorized logo and then silently ignore the removal, leaving the
    // discarded logo attached and available to the next submit.
    expect(logoBody).toMatch(/\n\s*setBrandLogo\(file\);/);
    expect(logoBody).not.toMatch(/if\s*\(\s*file\s*\)\s*\{?\s*setBrandLogo\(file\)/);
    expect(logoBody).not.toMatch(/setBrandLogo\(file\)\s*\|\|/);

    // Continuing after a removal must not carry the old validation message.
    expect(logoBody).toContain("setLogoError(\"\")");
  });

  it("invalidates the generated preview when the logo is taken back off", () => {
    const logoBody = functionBody("updateBrandLogo");

    // The preview row shows the logo, so clearing the logo has to drop the
    // preview it belongs to; otherwise the card keeps rendering a stale asset
    // row until the user happens to edit something else.
    const invalidate = /setPreview\(null\)|invalidatePreview\(\)/.exec(logoBody);
    expect(invalidate).not.toBeNull();

    // And it has to happen before the rejected-file return, so an unauthorized
    // or oversized replacement invalidates the old preview too.
    const rejected = logoBody.indexOf("if (file && !isSupportedProductAdImage(file))");
    expect(rejected).toBeGreaterThan(-1);
    expect(logoBody.indexOf(invalidate![0])).toBeLessThan(rejected);
  });

  it("keeps the logo in component state only, so nothing outlives the removal", () => {
    // Any of these would be a second place the logo survives `setBrandLogo(null)`
    // and could therefore still be submitted after the user removes it.
    expect(componentSource).not.toMatch(/\buseRef\b/);
    expect(componentSource).not.toMatch(/URL\.createObjectURL|revokeObjectURL|\bblob:/);
    expect(componentSource).not.toMatch(/FileReader|ArrayBuffer|Base64|base64/);
    expect(componentSource).not.toMatch(/localStorage|sessionStorage|indexedDB|caches\./);

    // The logo is named by a closed set of identifiers: one state slot, its
    // error, its help copy, and its handler. A fifth one — a label, snapshot,
    // or id kept beside the state — is how a removed logo can go stale, because
    // the removal clears `brandLogo` but nothing else.
    const logoIdentifiers = new Set(
      componentSource.match(/[A-Za-z_$][\w$]*[Ll]ogo[\w$]*|[Ll]ogo[A-Za-z_$][\w$]*/g) ?? [],
    );
    expect([...logoIdentifiers].sort()).toEqual([
      "brandLogo",
      "logoError",
      "logoHelp",
      "setBrandLogo",
      "setLogoError",
      "updateBrandLogo",
    ]);

    // The one logo state slot is a nullable `File`, and null is its cleared form.
    expect(componentSource).toMatch(/const \[brandLogo, setBrandLogo\] = useState<File \| null>\(null\);/);
  });

  it("gates every rendered logo reference on the live logo state", () => {
    // Two rows read the logo (the field confirmation and the preview card) and
    // both must be conditional on the state itself, never on a retained string.
    expect(componentSource.match(/\{brandLogo &&/g) ?? []).toHaveLength(2);
    expect(previewLogoRow()).toContain("{brandLogo.name}");
    expect(previewLogoRow()).not.toMatch(/preview\.|logoName/);

    // Whatever the mock assets are called, no asset reference is rendered before
    // a logo is attached or after it has been removed, in any supported locale.
    for (const locale of locales) {
      const html = renderToStaticMarkup(<ProductAdInputWizard locale={locale} />);
      expect(html).not.toMatch(/\.(png|jpe?g|svg|webp|gif)\b/i);
      expect(html).not.toContain(acceptedLogo.name);
      expect(html).not.toContain(acceptedPhoto.name);
      // The removable control itself is present in every locale, so the
      // add-then-remove journey is the same one in Arabic and Sorani as in
      // English, and it never becomes a field the draft cannot proceed without.
      expect(html.match(/id="[^"]+-brand-logo"/g)).toHaveLength(1);
      expect(html).not.toMatch(/id="[^"]+-brand-logo"[^>]*\brequired\b/);
    }
  });

  it("submits a draft that carries no image reference at all", () => {
    const submitBody = functionBody("createPreview");

    // The submitted draft is built from exactly four named fields; the logo is
    // not read into it, whether or not one was ever attached.
    expect(submitBody).toMatch(
      /buildProductAdMockPreview\(\s*\{\s*productName,\s*benefit,\s*channel,\s*format\s*\},\s*locale\s*\)/,
    );
    expect(submitBody).not.toMatch(/brandLogo|productPhoto|File\b|files\b/);

    const preview = buildProductAdMockPreview(draftAfterLogoRemoved, "en");
    expect(Object.keys(preview)).toEqual(Object.values(previewFields));
    // Neither side of the contract names an asset, so there is no slot a
    // discarded logo could be parked in.
    expect(Object.values(draftFields).join(",")).not.toMatch(/logo|photo|image|file|asset/i);
    expect(Object.values(previewFields).join(",")).not.toMatch(/logo|photo|image|file|asset/i);
    expect(Object.keys(draftAfterLogoRemoved)).toEqual(Object.values(draftFields));

    // Serializing the submitted draft can never surface the discarded logo, an
    // object URL for it, or a provider endpoint.
    const serialized = JSON.stringify(preview);
    for (const mock of [acceptedLogo, acceptedPhoto, unauthorizedLogo, oversizedLogo]) {
      expect(serialized).not.toContain(mock.name);
    }
    expect(serialized).not.toMatch(/blob:|data:|https?:\/\/|\/api\//);
    expect(Object.values(preview).every((value) => typeof value === "string")).toBe(true);
  });

  it("produces the identical draft whether the logo was removed or never chosen", () => {
    // Same text and output settings, so the add-then-remove journey and a run
    // that skipped the optional logo have to converge on one payload.
    const afterRemoval = buildProductAdMockPreview(draftAfterLogoRemoved, "en");
    const neverChosen = buildProductAdMockPreview({ ...draftAfterLogoRemoved }, "en");

    expect(afterRemoval).toEqual(neverChosen);
    expect(JSON.stringify(afterRemoval)).toBe(JSON.stringify(neverChosen));

    // Even a caller that hands the builder a stale logo property cannot leak it
    // into the preview: the builder is a function of the draft contract only.
    const staleDraft = { ...draftAfterLogoRemoved, brandLogo: acceptedLogo } as ProductAdDraftInput;
    expect(buildProductAdMockPreview(staleDraft, "en")).toEqual(neverChosen);
    expect(JSON.stringify(buildProductAdMockPreview(staleDraft, "en"))).not.toContain(acceptedLogo.name);

    // And the locale never changes that: no locale adds an asset field.
    for (const locale of locales) {
      expect(Object.keys(buildProductAdMockPreview(draftAfterLogoRemoved, locale))).toEqual([
        "headline",
        "caption",
        "channel",
        "format",
      ]);
    }
  });

  it("clears the logo when a replacement is unauthorized or oversized", () => {
    // Attachment is the only way a logo enters state, and the same bounded rule
    // that accepts the authorized logo rejects the removed-and-replaced cases.
    expect(isSupportedProductAdImage(acceptedLogo)).toBe(true);
    expect(isSupportedProductAdImage(unauthorizedLogo)).toBe(false);
    expect(isSupportedProductAdImage(oversizedLogo)).toBe(false);

    const logoBody = functionBody("updateBrandLogo");
    const rejectedBranch = logoBody.slice(
      logoBody.indexOf("if (file && !isSupportedProductAdImage(file))"),
      logoBody.indexOf("return;"),
    );
    // The rejected selection is dropped from state and from the control itself,
    // so the browser cannot keep reporting the discarded file.
    expect(rejectedBranch).toContain("setBrandLogo(null)");
    expect(rejectedBranch).toContain('event.currentTarget.value = ""');
    expect(rejectedBranch).toContain("setLogoError(copy.fileError)");
  });

  it("stays local-only across the whole journey", () => {
    // No transport anywhere in the component: a stale reference could not be
    // submitted even if one survived, because nothing is submitted at all.
    expect(componentSource).not.toMatch(/\bfetch\(|XMLHttpRequest|FormData|navigator\.send|WebSocket/);
    expect(componentSource).not.toContain("/api/");
    expect(componentSource).not.toMatch(/https?:\/\//);

    for (const locale of locales) {
      const html = renderToStaticMarkup(<ProductAdInputWizard locale={locale} />);
      expect(html).not.toContain("/api/");
      expect(html).not.toMatch(/https?:\/\//);
    }
  });
});
