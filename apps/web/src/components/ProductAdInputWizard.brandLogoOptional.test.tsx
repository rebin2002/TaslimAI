import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  buildProductAdMockPreview,
  isSupportedProductAdImage,
  MAX_PRODUCT_AD_IMAGE_BYTES,
  type ProductAdDraftInput,
} from "./ProductAdInputWizard";

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

/** The JSX attribute block of the `<input>` element that follows `idToken`. */
function inputBlock(idToken: string): string {
  const idIndex = componentSource.indexOf(idToken);
  const start = componentSource.indexOf("<input", idIndex);
  const end = componentSource.indexOf("/>", start);
  if (idIndex === -1 || start === -1 || end === -1) throw new Error(`missing input block for ${idToken}`);
  return componentSource.slice(start, end);
}

/** Deterministic local stub; no real File, upload, or provider access is involved. */
function stubImage(name: string, size = 1024, type = "image/png") {
  return { name, type, size };
}

const draftInput: ProductAdDraftInput = {
  productName: "Cedar & Silk",
  benefit: "Calm, soft skin",
  channel: "instagram",
  format: "square",
};

describe("ProductAdInputWizard brand-logo optionality", () => {
  it("replaces the product photo without ever touching the logo selection or its error", () => {
    const body = functionBody("updateProductPhoto");
    // Replacing a photo invalidates the preview — either directly or through a
    // shared invalidation helper — and that coupling is intended...
    expect(/setPreview\(null\)|invalidatePreview\(\)/.test(body)).toBe(true);
    // ...but the logo is optional, so a photo change must never reset it, mark
    // it invalid, or clear its file name.
    expect(body).not.toMatch(/setBrandLogo|setLogoError|\bbrandLogo\b|\blogoError\b/);
  });

  it("keeps the only logo-clearing branch inside the logo control itself", () => {
    const logoBody = functionBody("updateBrandLogo");
    expect(logoBody).toContain("setBrandLogo(null)");
    expect(logoBody).toContain("isSupportedProductAdImage");
    // Exactly one clear site in the whole component, and it lives in the logo
    // handler, so no other interaction — photo replacement or preview
    // invalidation — can silently drop an optional logo the user already chose.
    expect(componentSource.match(/setBrandLogo\(null\)/g) ?? []).toHaveLength(1);
    expect(componentSource.replace(logoBody, "")).not.toContain("setBrandLogo(null)");
  });

  it("does not drop the logo when the preview is cleared", () => {
    // The shared invalidation funnel added by the status-announcement work must
    // leave the optional logo alone.
    if (componentSource.includes("function invalidatePreview")) {
      expect(functionBody("invalidatePreview")).not.toMatch(/setBrandLogo|setLogoError/);
    }
    // Submitting builds the preview and touches neither asset state.
    const submitBody = functionBody("createPreview");
    expect(submitBody).toContain("buildProductAdMockPreview");
    expect(submitBody).not.toMatch(/setBrandLogo|setLogoError|setProductPhoto|setPhotoError/);
  });

  it("never marks the brand-logo control required while the product photo stays required", () => {
    const logoBlock = inputBlock("-brand-logo");
    const photoBlock = inputBlock("-product-photo");
    expect(logoBlock).not.toMatch(/\brequired\b/);
    expect(logoBlock).not.toContain("aria-required");
    expect(logoBlock).toContain("aria-describedby");
    expect(photoBlock).toMatch(/\brequired\b/);
  });

  it("applies the same bounded-image rule to an optional logo and builds the mock preview without one", () => {
    const logo = stubImage("brand-logo.png");
    const photo = stubImage("product-photo.jpg");
    const oversized = stubImage("huge-logo.png", MAX_PRODUCT_AD_IMAGE_BYTES + 1);
    const notAnImage = stubImage("notes.txt", 1024, "text/plain");

    expect(isSupportedProductAdImage(logo)).toBe(true);
    expect(isSupportedProductAdImage(photo)).toBe(true);
    expect(isSupportedProductAdImage(oversized)).toBe(false);
    expect(isSupportedProductAdImage(notAnImage)).toBe(false);

    // The minimal draft stays valid when the optional logo is skipped, and the
    // mock preview is a pure function of the text and output selections.
    const expected = {
      headline: "Cedar & Silk",
      caption: "Calm, soft skin — discover Cedar & Silk today.",
      channel: "Instagram",
      format: "Feed post · 1:1",
    };
    expect(buildProductAdMockPreview(draftInput, "en")).toEqual(expected);
    expect(buildProductAdMockPreview(draftInput, "en")).toEqual(buildProductAdMockPreview(draftInput, "en"));
  });
});
