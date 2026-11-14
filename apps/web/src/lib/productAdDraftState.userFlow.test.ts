import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  applyProductAdDraftEdit,
  blankProductAdDraft,
  buildProductAdDraftRestoredNotice,
  clearProductAdDraft,
  clearProductAdDraftForPrincipal,
  hasProductAdDraftContent,
  isProductAdDraftSubmittable,
  isStorableProductAdDraftAsset,
  MAX_PRODUCT_AD_DRAFT_NAME_BYTES,
  normalizeProductAdDraft,
  persistProductAdDraft,
  productAdDraftAssetNoticeKind,
  productAdDraftStorageKey,
  readProductAdDraft,
  resolveProductAdDraftPrincipal,
  restoreProductAdDraft,
  retireUnattributedProductAdDraft,
  toProductAdDraftPatch,
  type ProductAdDraftEdit,
  type ProductAdDraftState,
  type ProductAdDraftStore,
} from "./productAdDraftState";

/**
 * This file covers the one flow the wizard stack was missing: fill the form,
 * attach the required product photo and the optional brand logo, let the draft be
 * saved, reload the tab, and get the draft back without retyping anything. The
 * sibling suites pin the logo boundary (#785), the stale-reference boundary (#798),
 * the contrast (#738) and the announcement (#757) as isolated helpers; none of them
 * crossed the save/restore boundary.
 */

const wizardSource = readFileSync(
  fileURLToPath(new URL("../components/ProductAdInputWizard.tsx", import.meta.url)),
  "utf8",
);

const photo = { name: "cedar-silk.png", type: "image/png", size: 240_000 };
const logo = { name: "cedar-silk-logo.webp", type: "image/webp", size: 18_400 };
const secondPhoto = { name: "cedar-silk-flatlay.jpg", type: "image/jpeg", size: 1_200_000 };

const principal = { userId: "user-1", workspaceId: "workspace-1" };
const otherUserSameWorkspace = { userId: "user-2", workspaceId: "workspace-1" };

function makeStore(): ProductAdDraftStore & { entries: Map<string, string> } {
  const entries = new Map<string, string>();
  return {
    entries,
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => {
      entries.set(key, value);
    },
    removeItem: (key) => {
      entries.delete(key);
    },
  };
}

function edit(overrides: Partial<ProductAdDraftEdit> = {}): ProductAdDraftEdit {
  return {
    productName: "Cedar & Silk hand cream",
    benefit: "Calm, soft skin in one minute",
    channel: "instagram",
    format: "square",
    productPhoto: photo,
    brandLogo: logo,
    ...overrides,
  };
}

/** The wizard's own empty form, then one save per change, exactly as the UI fires them. */
function fillWizardThenSaveDraft(
  store: ProductAdDraftStore,
  at: (step: number) => number,
): ProductAdDraftState {
  let draft = blankProductAdDraft(at(0));
  // 1. The user starts typing the product name — no benefit yet, no files yet.
  draft = applyProductAdDraftEdit(draft, edit({ productName: "Cedar & Silk hand cream", benefit: "", productPhoto: null, brandLogo: null }), at(1));
  persistProductAdDraft(store, principal, draft);
  // 2. The required photo is attached.
  draft = applyProductAdDraftEdit(draft, edit({ benefit: "", productPhoto: photo, brandLogo: null }), at(2));
  persistProductAdDraft(store, principal, draft);
  // 3. The optional logo is added on top of the photo.
  draft = applyProductAdDraftEdit(draft, edit({ benefit: "", productPhoto: photo, brandLogo: logo }), at(3));
  persistProductAdDraft(store, principal, draft);
  // 4. The benefit and the output settings are edited; the files are still the same.
  draft = applyProductAdDraftEdit(draft, edit({ channel: "tiktok", format: "story" }), at(4));
  persistProductAdDraft(store, principal, draft);
  return draft;
}

describe("product ad draft user flow: save with photo + optional logo, restore on reload", () => {
  it("restores the filled form after a reload without any manual prompt text", () => {
    const store = makeStore();
    fillWizardThenSaveDraft(store, (step) => 1_700_000_000_000 + step);

    // A reload: a fresh module-level caller reads only what the store holds.
    const restored = restoreProductAdDraft(store, principal);

    expect(restored).not.toBeNull();
    // Nothing has to be typed again: every text field and setting comes back.
    expect(restored?.productName).toBe("Cedar & Silk hand cream");
    expect(restored?.benefit).toBe("Calm, soft skin in one minute");
    expect(restored?.channel).toBe("tiktok");
    expect(restored?.format).toBe("story");
    // The required photo and the optional logo are both named back.
    expect(restored?.productPhoto).toEqual({ name: photo.name, mime: photo.type, bytes: photo.size });
    expect(restored?.brandLogo).toEqual({ name: logo.name, mime: logo.type, bytes: logo.size });
    // The restored draft is immediately submittable once the files are re-attached.
    expect(isProductAdDraftSubmittable(restored!)).toBe(true);
    expect(productAdDraftAssetNoticeKind(restored!)).toBe("photoLogo");
  });

  it("writes the draft on the first keystroke, not only at submit time", () => {
    const store = makeStore();
    expect(readProductAdDraft(store, principal)).toBeNull();

    const draft = applyProductAdDraftEdit(
      blankProductAdDraft(1),
      edit({ productName: "C", benefit: "", productPhoto: null, brandLogo: null }),
      1,
    );
    expect(persistProductAdDraft(store, principal, draft)).not.toBeNull();
    expect(readProductAdDraft(store, principal)?.productName).toBe("C");
  });

  it("does not store a draft the user never filled", () => {
    const store = makeStore();
    const key = persistProductAdDraft(store, principal, blankProductAdDraft(5));
    expect(key).toBeNull();
    expect(store.entries.size).toBe(0);
    expect(hasProductAdDraftContent(blankProductAdDraft(5))).toBe(false);
  });

  it("keeps the optional logo when the photo is replaced mid-draft", () => {
    const store = makeStore();
    let draft = fillWizardThenSaveDraft(store, (step) => 100 + step);
    draft = applyProductAdDraftEdit(draft, edit({ productPhoto: secondPhoto }), 200);
    persistProductAdDraft(store, principal, draft);

    const restored = restoreProductAdDraft(store, principal);
    expect(restored?.productPhoto?.name).toBe(secondPhoto.name);
    expect(restored?.brandLogo).toEqual({ name: logo.name, mime: logo.type, bytes: logo.size });
  });

  it("drops the logo from the stored draft when the user removes it", () => {
    const store = makeStore();
    const draft = applyProductAdDraftEdit(fillWizardThenSaveDraft(store, (step) => 100 + step), edit({ brandLogo: null }), 300);
    persistProductAdDraft(store, principal, draft);

    const restored = restoreProductAdDraft(store, principal);
    expect(restored?.brandLogo).toBeNull();
    expect(productAdDraftAssetNoticeKind(restored!)).toBe("photoOnly");
    expect(buildProductAdDraftRestoredNotice(restored!, "en")).not.toContain("brand logo");
  });

  it("stores descriptors only: no image bytes, no provider or API surface", () => {
    const store = makeStore();
    fillWizardThenSaveDraft(store, (step) => 100 + step);
    const key = productAdDraftStorageKey(principal)!;
    const payload = store.entries.get(key)!;

    // A 240 KB PNG cannot be in a payload of this size.
    expect(new TextEncoder().encode(payload).length).toBeLessThan(2048);
    expect(payload).not.toMatch(/data:image/);
    expect(payload).not.toMatch(/base64/i);
    expect(payload).not.toMatch(/https?:\/\//);
    expect(payload).not.toContain("/api/");
    expect(payload).not.toContain("blob:");
    // Only the fields the flow needs are persisted.
    expect(Object.keys(JSON.parse(payload)).sort()).toEqual([
      "benefit",
      "brandLogo",
      "channel",
      "format",
      "productName",
      "productPhoto",
      "savedAt",
      "savedByUser",
      "savedByWorkspace",
      "version",
    ]);
  });

  it("scopes the draft by identity as well as workspace, so an account switch cannot restore it", () => {
    const store = makeStore();
    fillWizardThenSaveDraft(store, (step) => 100 + step);

    expect(productAdDraftStorageKey(principal)).toBe("taslim:draft:product-ad:user-1:workspace-1");
    expect(productAdDraftStorageKey(otherUserSameWorkspace)).not.toBe(productAdDraftStorageKey(principal));
    expect(restoreProductAdDraft(store, otherUserSameWorkspace)).toBeNull();

    // The second identity writing its own draft leaves the first untouched.
    const theirs = applyProductAdDraftEdit(
      blankProductAdDraft(1),
      edit({ productName: "Someone else's soap", productPhoto: null, brandLogo: null }),
      1,
    );
    persistProductAdDraft(store, otherUserSameWorkspace, theirs);
    expect(restoreProductAdDraft(store, otherUserSameWorkspace)?.productName).toBe("Someone else's soap");
    expect(restoreProductAdDraft(store, principal)?.productName).toBe("Cedar & Silk hand cream");
  });

  it("refuses to write or read for anonymous or partial identities", () => {
    const store = makeStore();
    const draft = applyProductAdDraftEdit(blankProductAdDraft(1), edit(), 1);

    for (const anonymous of [
      { userId: null, workspaceId: "workspace-1" },
      { userId: "user-1", workspaceId: null },
      { userId: "anonymous", workspaceId: "workspace-1" },
      { userId: " guest ", workspaceId: "workspace-1" },
      { userId: "user-1", workspaceId: "" },
      { userId: "user-1\u0000", workspaceId: "workspace-1" },
      null,
      undefined,
    ]) {
      expect(productAdDraftStorageKey(anonymous)).toBeNull();
      expect(resolveProductAdDraftPrincipal(anonymous)).toBeNull();
      expect(persistProductAdDraft(store, anonymous, draft)).toBeNull();
      expect(restoreProductAdDraft(store, anonymous)).toBeNull();
      expect(clearProductAdDraftForPrincipal(store, anonymous)).toBe(false);
    }
    expect(store.entries.size).toBe(0);
  });

  it("keeps ids with key separators in separate entries", () => {
    const colonUser = { userId: "user:1", workspaceId: "workspace-1" };
    const slashUser = { userId: "user", workspaceId: "1:workspace-1" };
    expect(productAdDraftStorageKey(colonUser)).toBe("taslim:draft:product-ad:user%3A1:workspace-1");
    expect(productAdDraftStorageKey(slashUser)).toBe("taslim:draft:product-ad:user:1%3Aworkspace-1");
    expect(productAdDraftStorageKey(colonUser)).not.toBe(productAdDraftStorageKey(slashUser));
  });

  it("purges unreadable or contentless entries instead of restoring them", () => {
    const key = productAdDraftStorageKey(principal)!;
    for (const garbage of ["{", "null", "[]", '""', '{"productName":"   "}']) {
      const store = makeStore();
      store.setItem(key, garbage);
      expect(readProductAdDraft(store, principal)).toBeNull();
      expect(store.entries.has(key)).toBe(false);
    }
  });

  it("normalises a tampered draft down to the wizard defaults", () => {
    const store = makeStore();
    fillWizardThenSaveDraft(store, (step) => 100 + step);
    const key = productAdDraftStorageKey(principal)!;
    store.setItem(
      key,
      JSON.stringify({
        version: -3,
        savedAt: Number.MAX_SAFE_INTEGER,
        productName: "x".repeat(500),
        benefit: "y".repeat(900),
        channel: "instagram; drop-table",
        format: "portrait",
        // An oversized attachment is not trusted on read even though the wizard
        // rejected it on selection.
        productPhoto: { name: "huge.png", mime: "image/png", bytes: MAX_PRODUCT_AD_DRAFT_NAME_BYTES + 1 },
        brandLogo: { name: 42, mime: "image/webp", bytes: 10 },
      }),
    );

    const restored = readProductAdDraft(store, principal)!;
    expect(restored.productName).toHaveLength(120);
    expect(restored.benefit).toHaveLength(280);
    expect(restored.channel).toBe("instagram");
    expect(restored.format).toBe("square");
    expect(restored.productPhoto).toBeNull();
    expect(restored.brandLogo).toBeNull();
    // The savedAt ceiling keeps a sane value rather than discarding the draft.
    expect(restored.savedAt).toBe(Number.MAX_SAFE_INTEGER);
    expect(restored.version).toBe(1);
  });

  it("never surfaces a draft whose owner fields disagree with the key", () => {
    const store = makeStore();
    fillWizardThenSaveDraft(store, (step) => 100 + step);
    const key = productAdDraftStorageKey(principal)!;
    const payload = JSON.parse(store.entries.get(key)!);
    store.setItem(key, JSON.stringify({ ...payload, savedByUser: encodeURIComponent("user-2") }));

    expect(readProductAdDraft(store, principal)).toBeNull();
    // Purged by default so the entry cannot be probed again; opt-out keeps it.
    expect(store.entries.has(key)).toBe(false);

    store.setItem(key, JSON.stringify({ ...payload, savedByUser: encodeURIComponent("user-2") }));
    expect(readProductAdDraft(store, principal, { purgeOnOwnerMismatch: false })).toBeNull();
    expect(store.entries.has(key)).toBe(true);
  });

  it("retires an unattributed legacy draft without exposing its content", () => {
    const store = makeStore();
    const legacyKey = "taslim:product-ad-draft:workspace-1";
    store.setItem(legacyKey, JSON.stringify({ productName: "Private product", productPhoto: { name: "secret.png", mime: "image/png", bytes: 10 } }));

    expect(retireUnattributedProductAdDraft(store, legacyKey)).toBe(true);
    expect(store.entries.has(legacyKey)).toBe(false);
    // An attributed entry belongs to someone and is left alone.
    const key = productAdDraftStorageKey(principal)!;
    fillWizardThenSaveDraft(store, (step) => 100 + step);
    expect(retireUnattributedProductAdDraft(store, key)).toBe(false);
    expect(store.entries.has(key)).toBe(true);
    expect(retireUnattributedProductAdDraft(store, "taslim:draft:product-ad:user-1:workspace-1")).toBe(false);
  });

  it("clears the draft on submit or discard", () => {
    const store = makeStore();
    fillWizardThenSaveDraft(store, (step) => 100 + step);
    clearProductAdDraft(store, principal);
    expect(restoreProductAdDraft(store, principal)).toBeNull();
    // A second clear is a no-op, not a throw.
    clearProductAdDraft(store, principal);
    clearProductAdDraft(store, null);
    clearProductAdDraft(undefined, principal);
  });

  it("describes the reload honestly in every wizard locale", () => {
    const store = makeStore();
    const draft = fillWizardThenSaveDraft(store, (step) => 100 + step);

    const english = buildProductAdDraftRestoredNotice(draft, "en");
    expect(english).toContain("Cedar & Silk hand cream");
    expect(english).toContain("Re-attach the product photo and the brand logo");
    expect(buildProductAdDraftRestoredNotice(draft, "ar")).toContain("شعار العلامة التجارية");
    expect(buildProductAdDraftRestoredNotice(draft, "ku")).toContain("لۆگۆی براند");
    // No name, nothing to announce.
    expect(buildProductAdDraftRestoredNotice(blankProductAdDraft(1), "en")).toBe("");
    // Each locale states the re-attach rule; none claims the files came back.
    for (const locale of ["en", "ar", "ku"] as const) {
      expect(buildProductAdDraftRestoredNotice(draft, locale)).not.toMatch(/uploaded|provider/i);
    }
  });

  it("round-trips the patch shape the wizard would adopt unchanged", () => {
    const store = makeStore();
    const draft = fillWizardThenSaveDraft(store, (step) => 100 + step);
    const restored = restoreProductAdDraft(store, principal)!;
    const patch = toProductAdDraftPatch(restored);

    expect(patch).toEqual({
      productName: "Cedar & Silk hand cream",
      benefit: "Calm, soft skin in one minute",
      channel: "tiktok",
      format: "story",
      productPhoto: { name: photo.name, mime: photo.type, bytes: photo.size },
      brandLogo: { name: logo.name, mime: logo.type, bytes: logo.size },
    });
    // Saving the restored patch again is idempotent.
    const again = applyProductAdDraftEdit(restored, { ...patch, productPhoto: null, brandLogo: null }, draft.savedAt);
    expect(again.productPhoto).toBeNull();
    expect(again.brandLogo).toBeNull();
  });

  it("agrees with the wizard's own file rule and its non-autofilling inputs", () => {
    // The stored-descriptor ceiling is the wizard's selection ceiling.
    expect(isStorableProductAdDraftAsset({ type: "image/png", size: MAX_PRODUCT_AD_DRAFT_NAME_BYTES })).toBe(true);
    expect(isStorableProductAdDraftAsset({ type: "image/png", size: MAX_PRODUCT_AD_DRAFT_NAME_BYTES + 1 })).toBe(false);
    expect(isStorableProductAdDraftAsset({ type: "text/plain", size: 128 })).toBe(false);
    expect(isStorableProductAdDraftAsset(null)).toBe(false);

    // The real UI behaviour the notice depends on: neither file input pre-fills a
    // value, so a reload genuinely starts with no attached files.
    expect(wizardSource).toContain('id={`${id}-product-photo`}');
    expect(wizardSource).toContain('id={`${id}-brand-logo`}');
    expect(wizardSource).not.toMatch(/-product-photo`[\s\S]{0,320}defaultValue/);
    expect(wizardSource).not.toMatch(/-brand-logo`[\s\S]{0,320}defaultValue/);
    expect(wizardSource).not.toMatch(/initialDraft|restoredDraft/);
  });

  it("keeps a text-only draft restorable but not submittable", () => {
    const store = makeStore();
    const draft = applyProductAdDraftEdit(blankProductAdDraft(1), edit({ productPhoto: null, brandLogo: null }), 1);
    persistProductAdDraft(store, principal, draft);

    const restored = restoreProductAdDraft(store, principal)!;
    expect(restored.productName).toBe(draft.productName);
    expect(isProductAdDraftSubmittable(restored)).toBe(false);
    expect(productAdDraftAssetNoticeKind(restored)).toBe("none");
    expect(buildProductAdDraftRestoredNotice(restored, "en")).not.toContain("Re-attach");
  });

  it("preserves the normalization defaults and rejects non-objects", () => {
    const defaults = normalizeProductAdDraft(undefined);
    expect(defaults).toEqual(blankProductAdDraft(0));
    expect(normalizeProductAdDraft("string").productName).toBe("");
    expect(normalizeProductAdDraft({ productName: "  A  ", benefit: "  B  " }).productName).toBe("A");
  });
});
