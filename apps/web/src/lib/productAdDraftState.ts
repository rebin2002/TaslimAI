/**
 * Product ad wizard draft persistence (PR #733 / PR #736 follow-up).
 *
 * Gap this module closes: every other studio persists its unsent draft through a
 * `*StudioState` module that its view both reads on mount and writes on every edit
 * (`socialStudioState.ts` is the reference). The product ad wizard kept the whole
 * draft — including the required product photo and the optional brand logo — in
 * `useState` only, so a browser reload discarded the form and the user had to type
 * the product name and benefit again by hand.
 *
 * Design constraints, in the order the review asked them to be honoured:
 * - Provider-free and network-free: nothing here uploads a file, calls an API route,
 *   or touches an image provider. Only a small, bounded descriptor of each chosen
 *   image is stored, never its bytes.
 * - SSR-safe: every storage entry point takes an explicit store, so a server
 *   component can import the module without ever reaching for `window`.
 * - Identity-bound keys, matching the policy proposed in PR #800 for issue #688:
 *   `taslim:draft:<namespace>:<user>:<workspace>`. A workspace id is not an
 *   authentication boundary, so an anonymous or missing identity is a no-op rather
 *   than a fall-back to a workspace-only key.
 * - Adoptable without a merge conflict: this module does not import
 *   `ProductAdInputWizard.tsx` (owned by PR #733, currently also carrying PR #738
 *   and PR #757) and it does not import `sessionDraftScope.ts` (owned by PR #800).
 *   The `ProductAdDraftStore` shape below is structurally compatible with PR #800's
 *   `SessionDraftStore`, so the wizard can move onto that primitive with no change
 *   to this module's public API. The image rules are restated from
 *   `isSupportedProductAdImage` deliberately and pinned by a test so the two
 *   boundaries cannot drift apart silently.
 */
import { locales, type Locale } from "@/lib/i18n";

/** Local-only wizard settings that belong to the draft, not to the stored payload. */
export type ProductAdChannel = "instagram" | "facebook" | "tiktok";
export type ProductAdFormat = "square" | "story" | "landscape";

/**
 * What survives a reload for one chosen image: its identity for the user (name),
 * plus the two fields the wizard already validates on. The bytes never do, which
 * is what keeps the wizard's "no files are uploaded" promise true while it is
 * persisted as well as while it is previewed.
 */
export type ProductAdDraftAsset = {
  name: string;
  mime: string;
  bytes: number;
};

export type ProductAdDraftState = {
  version: number;
  savedAt: number;
  productName: string;
  benefit: string;
  channel: ProductAdChannel;
  format: ProductAdFormat;
  /** The required product photo, when one has been chosen. */
  productPhoto: ProductAdDraftAsset | null;
  /** The optional brand logo, when one has been chosen. */
  brandLogo: ProductAdDraftAsset | null;
};

/** Every editable value in the wizard form, exactly as the inputs hold it. */
export type ProductAdDraftEdit = {
  productName: string;
  benefit: string;
  channel: ProductAdChannel;
  format: ProductAdFormat;
  productPhoto: Pick<File, "name" | "type" | "size"> | null;
  brandLogo: Pick<File, "name" | "type" | "size"> | null;
};

/** A draft minus its stored metadata, which is what the wizard writes on each edit. */
export type ProductAdDraftPatch = Omit<ProductAdDraftState, "version" | "savedAt">;

export const PRODUCT_AD_DRAFT_VERSION = 1;
export const PRODUCT_AD_DRAFT_NAMESPACE = "product-ad";
export const MAX_PRODUCT_AD_DRAFT_NAME_BYTES = 10 * 1024 * 1024;

/** Kept in step with the wizard's own inputs and its 10 MB image ceiling. */
const limits = {
  productName: 120,
  benefit: 280,
  assetName: 240,
  mime: 100,
};
const maxAssetNameBytes = 8 * 1024;
const keyRoot = "taslim:draft:";
const maxIdentityLength = 512;
const draftChannels: readonly string[] = ["instagram", "facebook", "tiktok"];
const draftFormats: readonly string[] = ["square", "story", "landscape"];
const anonymousIdentitySentinels = new Set([
  "anonymous",
  "anon",
  "guest",
  "signed-out",
  "unknown",
  "null",
  "undefined",
  "default",
]);

/** Structural subset of `Storage`, mirroring PR #800's store contract. */
export type ProductAdDraftStore = {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
};

export type ProductAdDraftPrincipal = {
  userId: string | null | undefined;
  workspaceId: string | null | undefined;
};

export type ResolvedProductAdDraftPrincipal = {
  userId: string;
  workspaceId: string;
};

export type ProductAdDraftAssetNoticeKind = "none" | "photoOnly" | "photoLogo";

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function boundedText(value: unknown, limit: number): string {
  if (typeof value !== "string") return "";
  const trimmed = value.trim();
  if (!trimmed) return "";
  return Array.from(trimmed).slice(0, limit).join("");
}

/** Control characters and lone surrogates cannot be encoded or enumerated safely. */
function validIdentityToken(value: string | null | undefined): value is string {
  if (typeof value !== "string") return false;
  const trimmed = value.trim();
  if (!trimmed) return false;
  if (trimmed.length > maxIdentityLength) return false;
  if (/[\u0000-\u001f\u007f]/.test(trimmed)) return false;
  if (/[\uD800-\uDBFF](?![\uDC00-\uDFFF])/.test(trimmed)) return false;
  if (/(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/.test(trimmed)) return false;
  if (anonymousIdentitySentinels.has(trimmed.toLowerCase())) return false;
  return true;
}

export function resolveProductAdDraftPrincipal(
  principal: ProductAdDraftPrincipal | null | undefined,
): ResolvedProductAdDraftPrincipal | null {
  if (!principal) return null;
  if (!validIdentityToken(principal.userId) || !validIdentityToken(principal.workspaceId)) return null;
  return { userId: principal.userId.trim(), workspaceId: principal.workspaceId.trim() };
}

/** Percent-encoded segments stop ids containing `:` or `/` from colliding across segments. */
function identitySegment(value: string) {
  return encodeURIComponent(value.trim());
}

/**
 * Returns null for an unproven identity so a caller can never fall through to an
 * identity-less key that a second account in the same tab could read back.
 */
export function productAdDraftStorageKey(
  principal: ProductAdDraftPrincipal | null | undefined,
): string | null {
  const resolved = resolveProductAdDraftPrincipal(principal);
  if (!resolved) return null;
  return `${keyRoot}${identitySegment(PRODUCT_AD_DRAFT_NAMESPACE)}:${identitySegment(resolved.userId)}:${identitySegment(resolved.workspaceId)}`;
}

/**
 * The same two rules the wizard applies before it accepts a file
 * (`isSupportedProductAdImage`): a real image type inside the 10 MB ceiling.
 */
export function isStorableProductAdDraftAsset(
  file: Pick<File, "type" | "size"> | null | undefined,
): file is Pick<File, "type" | "size"> {
  if (!file) return false;
  return typeof file.type === "string" && file.type.startsWith("image/") && file.size <= MAX_PRODUCT_AD_DRAFT_NAME_BYTES;
}

function toDraftAsset(file: ProductAdDraftEdit["productPhoto"]): ProductAdDraftAsset | null {
  if (!isStorableProductAdDraftAsset(file) || typeof file.name !== "string") return null;
  const name = boundedText(file.name, limits.assetName);
  const mime = boundedText(file.type, limits.mime);
  if (!name || !mime) return null;
  if (new TextEncoder().encode(name).length > maxAssetNameBytes) return null;
  const bytes = Number.isFinite(file.size) && file.size >= 0 && file.size <= MAX_PRODUCT_AD_DRAFT_NAME_BYTES
    ? Math.trunc(file.size)
    : 0;
  return { name, mime, bytes };
}

/**
 * Normalises an unknown payload — a stored entry or a patch — to a draft. Anything
 * unusable degrades to the wizard's own defaults instead of being trusted, so a
 * tampered or half-written entry can never re-enable a provider path.
 */
export function normalizeProductAdDraft(raw: unknown): ProductAdDraftState {
  if (!isRecord(raw)) return blankProductAdDraft();
  const channel = typeof raw.channel === "string" && draftChannels.includes(raw.channel)
    ? (raw.channel as ProductAdChannel)
    : "instagram";
  const format = typeof raw.format === "string" && draftFormats.includes(raw.format)
    ? (raw.format as ProductAdFormat)
    : "square";
  const version = typeof raw.version === "number" && Number.isFinite(raw.version) && raw.version > 0
    ? Math.trunc(raw.version)
    : PRODUCT_AD_DRAFT_VERSION;
  const savedAt = typeof raw.savedAt === "number" && Number.isFinite(raw.savedAt) && raw.savedAt >= 0
    ? Math.trunc(raw.savedAt)
    : 0;
  return {
    version,
    savedAt,
    productName: boundedText(raw.productName, limits.productName),
    benefit: boundedText(raw.benefit, limits.benefit),
    channel,
    format,
    productPhoto: normalizeProductAdDraftAsset(raw.productPhoto),
    brandLogo: normalizeProductAdDraftAsset(raw.brandLogo),
  };
}

function normalizeProductAdDraftAsset(raw: unknown): ProductAdDraftAsset | null {
  if (!isRecord(raw)) return null;
  const asset = toDraftAsset({
    name: typeof raw.name === "string" ? raw.name : "",
    type: typeof raw.mime === "string" ? raw.mime : "",
    size: typeof raw.bytes === "number" ? raw.bytes : Number.NaN,
  });
  return asset;
}

/** Stored payload: the draft plus the provenance the reader checks before trusting it. */
type ProductAdDraftEnvelope = ProductAdDraftState & {
  savedByUser: string;
  savedByWorkspace: string;
};

export function blankProductAdDraft(savedAt = 0): ProductAdDraftState {
  return {
    version: PRODUCT_AD_DRAFT_VERSION,
    savedAt,
    productName: "",
    benefit: "",
    channel: "instagram",
    format: "square",
    productPhoto: null,
    brandLogo: null,
  };
}

/**
 * The save half of the user flow: one call per wizard edit. The optional logo is
 * carried exactly as chosen — replacing the photo is the wizard's own
 * `updateProductPhoto`, which never touches the logo state, so a draft edit must
 * not invent a logo clear either.
 */
export function applyProductAdDraftEdit(
  current: ProductAdDraftState | null,
  edit: ProductAdDraftEdit,
  savedAt: number,
): ProductAdDraftState {
  const previous = current ?? blankProductAdDraft(savedAt);
  return normalizeProductAdDraft({
    version: previous.version,
    savedAt: Number.isFinite(savedAt) ? Math.trunc(savedAt) : 0,
    productName: edit.productName,
    benefit: edit.benefit,
    channel: edit.channel,
    format: edit.format,
    productPhoto: toDraftAsset(edit.productPhoto),
    brandLogo: toDraftAsset(edit.brandLogo),
  });
}

/** The form values the wizard needs to be back in its real starting configuration. */
export function toProductAdDraftPatch(state: ProductAdDraftState): ProductAdDraftPatch {
  return {
    productName: state.productName,
    benefit: state.benefit,
    channel: state.channel,
    format: state.format,
    productPhoto: state.productPhoto,
    brandLogo: state.brandLogo,
  };
}

export function persistProductAdDraft(
  store: ProductAdDraftStore | null | undefined,
  principal: ProductAdDraftPrincipal | null | undefined,
  state: ProductAdDraftState,
): string | null {
  const key = productAdDraftStorageKey(principal);
  const resolved = resolveProductAdDraftPrincipal(principal);
  if (!store || !key || !resolved) return null;
  const normalized = normalizeProductAdDraft(state);
  // An empty draft is not worth a storage write, and refusing to store it keeps a
  // later restore from reporting "restored" for a form the user never filled.
  if (!hasProductAdDraftContent(normalized)) return null;
  const envelope: ProductAdDraftEnvelope = {
    ...normalized,
    savedByUser: identitySegment(resolved.userId),
    savedByWorkspace: identitySegment(resolved.workspaceId),
  };
  try {
    store.setItem(key, JSON.stringify(envelope));
    return key;
  } catch {
    // Quota exceeded or a blocked store: the wizard keeps working in memory.
    return null;
  }
}

export function readProductAdDraft(
  store: ProductAdDraftStore | null | undefined,
  principal: ProductAdDraftPrincipal | null | undefined,
  options: Readonly<{ purgeOnOwnerMismatch?: boolean }> = {},
): ProductAdDraftState | null {
  const key = productAdDraftStorageKey(principal);
  const resolved = resolveProductAdDraftPrincipal(principal);
  if (!store || !key || !resolved) return null;
  let raw: string | null;
  try {
    raw = store.getItem(key);
  } catch {
    return null;
  }
  if (!raw) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    clearProductAdDraft(store, key);
    return null;
  }
  if (!isRecord(parsed)) {
    clearProductAdDraft(store, key);
    return null;
  }
  const storedUser = typeof parsed.savedByUser === "string" ? parsed.savedByUser : null;
  const storedWorkspace = typeof parsed.savedByWorkspace === "string" ? parsed.savedByWorkspace : null;
  const attributed = storedUser !== null || storedWorkspace !== null;
  const owned = attributed
    && storedUser === encodeURIComponent(resolved.userId)
    && storedWorkspace === encodeURIComponent(resolved.workspaceId);
  if (attributed && !owned) {
    // A foreign entry is never surfaced; it is removed unless the caller opted out.
    if (options.purgeOnOwnerMismatch !== false) clearProductAdDraft(store, key);
    return null;
  }
  const draft = normalizeProductAdDraft(parsed);
  if (!hasProductAdDraftContent(draft)) {
    clearProductAdDraft(store, key);
    return null;
  }
  return draft;
}

/**
 * The restore half of the user flow. A draft with a product name is restored
 * automatically — the user never retypes the product name or the benefit — and it
 * is restored into the form only, never into a built preview, so a reload cannot
 * re-announce a preview the user has not asked for again.
 */
export function restoreProductAdDraft(
  store: ProductAdDraftStore | null | undefined,
  principal: ProductAdDraftPrincipal | null | undefined,
): ProductAdDraftState | null {
  return readProductAdDraft(store, principal);
}

export function clearProductAdDraft(
  store: ProductAdDraftStore | null | undefined,
  principalOrKey: ProductAdDraftPrincipal | string | null | undefined,
): void {
  const key = typeof principalOrKey === "string"
    ? principalOrKey
    : productAdDraftStorageKey(principalOrKey);
  if (!store || !key) return;
  try {
    store.removeItem(key);
  } catch {
    // Nothing to do: the entry is already unreachable for this caller.
  }
}

export function clearProductAdDraftForPrincipal(
  store: ProductAdDraftStore | null | undefined,
  principal: ProductAdDraftPrincipal | null | undefined,
): boolean {
  const key = productAdDraftStorageKey(principal);
  if (!store || !key) return false;
  clearProductAdDraft(store, key);
  return true;
}

/**
 * A draft written before keys were identity-bound carries no provenance. It is
 * retired without ever surfacing its content, so an unattributed entry cannot be
 * adopted by whichever identity happens to ask next.
 */
export function retireUnattributedProductAdDraft(
  store: ProductAdDraftStore | null | undefined,
  key: string,
): boolean {
  if (!store || !key) return false;
  let raw: string | null;
  try {
    raw = store.getItem(key);
  } catch {
    return false;
  }
  if (!raw) return false;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    // Unreadable already means unusable; remove it so it cannot be probed again.
    clearProductAdDraft(store, key);
    return true;
  }
  if (!isRecord(parsed)) return false;
  if (typeof parsed.savedByUser === "string" || typeof parsed.savedByWorkspace === "string") return false;
  clearProductAdDraft(store, key);
  return true;
}

/** A draft is worth keeping once the user has entered the minimum the wizard asks for. */
export function hasProductAdDraftContent(state: ProductAdDraftState): boolean {
  return state.productName.trim().length > 0
    || state.benefit.trim().length > 0
    || state.productPhoto !== null
    || state.brandLogo !== null;
}

/** A draft is submittable when it holds the two things the wizard marks required. */
export function isProductAdDraftSubmittable(state: ProductAdDraftState): boolean {
  return state.productName.trim().length > 0 && state.productPhoto !== null;
}

export function productAdDraftAssetNoticeKind(state: ProductAdDraftState): ProductAdDraftAssetNoticeKind {
  if (state.productPhoto && state.brandLogo) return "photoLogo";
  if (state.productPhoto) return "photoOnly";
  return "none";
}

type ProductAdDraftCopy = {
  restored: (productName: string) => string;
  reattachPhoto: string;
  reattachPhotoAndLogo: string;
};

/** Every wizard locale must be able to describe a restored draft. */
const copyByLocale: Record<(typeof locales)[number], ProductAdDraftCopy> = {
  en: {
    restored: (productName) => `Draft restored: ${productName}.`,
    reattachPhoto: "Re-attach the product photo to continue.",
    reattachPhotoAndLogo: "Re-attach the product photo and the brand logo to continue.",
  },
  ar: {
    restored: (productName) => `تمت استعادة المسودة: ${productName}.`,
    reattachPhoto: "أعد إرفاق صورة المنتج للمتابعة.",
    reattachPhotoAndLogo: "أعد إرفاق صورة المنتج وشعار العلامة التجارية للمتابعة.",
  },
  ku: {
    restored: (productName) => `ڕەشنووس ڕێکخرایەوە: ${productName}.`,
    reattachPhoto: "بۆ بەردەوامبوون وێنەی بەرهەم دووبارە لەگەڵدا بخە.",
    reattachPhotoAndLogo: "بۆ بەردەوامبوون وێنەی بەرهەم و لۆگۆی براند دووبارە لەگەڵدا بخە.",
  },
} satisfies Record<Locale, ProductAdDraftCopy>;

/**
 * Tells the truth about a reload: the wizard stores descriptors only, so the chosen
 * images are named back to the user but must be selected again. A caller must not
 * render a restored draft as if the files themselves came back.
 */
export function buildProductAdDraftRestoredNotice(
  state: ProductAdDraftState,
  locale: Locale = "en",
): string {
  const copy = copyByLocale[locale];
  const productName = state.productName.trim();
  if (!productName) return "";
  const head = copy.restored(productName);
  switch (productAdDraftAssetNoticeKind(state)) {
    case "photoLogo":
      return `${head} ${copy.reattachPhotoAndLogo}`;
    case "photoOnly":
      return `${head} ${copy.reattachPhoto}`;
    case "none":
      return head;
  }
}
