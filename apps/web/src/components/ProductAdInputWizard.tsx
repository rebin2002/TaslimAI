"use client";

import { useEffect, useId, useState, type ChangeEvent, type FormEvent } from "react";
import { applyProductAdDraftEdit, blankProductAdDraft, buildProductAdDraftRestoredNotice, clearProductAdDraft, hasProductAdDraftContent, persistProductAdDraft, productAdDraftStorageKey, productAdPendingAssetNoticeKind, restoreProductAdDraft, type ProductAdAttachedAssets, type ProductAdDraftAsset, type ProductAdDraftAssetNoticeKind, type ProductAdDraftPrincipal, type ProductAdDraftState } from "@/lib/productAdDraftState";
import { localeDirection, localeTag, type Locale } from "@/lib/i18n";
import styles from "./ProductAdInputWizard.module.css";

export type ProductAdChannel = "instagram" | "facebook" | "tiktok";
export type ProductAdFormat = "square" | "story" | "landscape";

export const defaultProductAdOutput: Readonly<{ channel: ProductAdChannel; format: ProductAdFormat }> = {
  channel: "instagram",
  format: "square",
};

export const MAX_PRODUCT_AD_IMAGE_BYTES = 10 * 1024 * 1024;

export type ProductAdDraftInput = {
  productName: string;
  benefit: string;
  channel: ProductAdChannel;
  format: ProductAdFormat;
};

export type ProductAdMockPreview = {
  headline: string;
  caption: string;
  channel: string;
  format: string;
};

/** Accessible preview lifecycle. The announcement region stays mounted between edits. */
export type ProductAdPreviewStatus = "idle" | "ready" | "cleared";

export function nextProductAdPreviewStatus(current: ProductAdPreviewStatus, action: "edited" | "generated"): ProductAdPreviewStatus {
  if (action === "generated") return "ready";
  return current === "ready" ? "cleared" : current;
}


type ProductAdCopy = {
  eyebrow: string;
  title: string;
  intro: string;
  productName: string;
  productNamePlaceholder: string;
  benefit: string;
  benefitPlaceholder: string;
  productPhoto: string;
  photoHelp: string;
  brandLogo: string;
  logoHelp: string;
  channel: string;
  format: string;
  channels: Record<ProductAdChannel, string>;
  formats: Record<ProductAdFormat, string>;
  createPreview: string;
  fileError: string;
  previewTitle: string;
  previewReady: (channel: string, format: string) => string;
  previewCleared: string;
  previewEmptyTitle: string;
  previewEmpty: string;
  mockBadge: string;
  localOnly: string;
  /** Masked first paint of the authenticated route: announced, so it must be localized. */
  maskRestoring: string;
  /** Accessible name of the local artwork region, read instead of the image itself. */
  previewArtworkLabel: string;
  captionWithBenefit: (benefit: string, productName: string) => string;
  captionWithoutBenefit: (productName: string) => string;
};

const copyByLocale: Record<Locale, ProductAdCopy> = {
  en: {
    eyebrow: "Product advertisement",
    title: "Start with your product.",
    intro: "Add one product photo and a few words. This demo creates a local draft preview only—nothing is uploaded or sent to an AI provider.",
    productName: "Product name",
    productNamePlaceholder: "e.g., Cedar & Silk hand cream",
    benefit: "One thing to highlight (optional)",
    benefitPlaceholder: "A key benefit, offer, or reason to choose it",
    productPhoto: "Product photo (required)",
    photoHelp: "Choose one product image. JPEG, PNG, or WebP, up to 10 MB.",
    brandLogo: "Brand logo (optional)",
    logoHelp: "Add a logo if you have one. It stays local in this preview.",
    channel: "Output channel",
    format: "Output format",
    channels: { instagram: "Instagram", facebook: "Facebook", tiktok: "TikTok" },
    formats: { square: "Feed post · 1:1", story: "Story · 9:16", landscape: "Landscape · 16:9" },
    createPreview: "Create mock preview",
    fileError: "Choose an image file no larger than 10 MB.",
    previewTitle: "Draft preview",
    previewReady: (channel, format) => `Draft preview ready for ${channel} in ${format}.`,
    previewCleared: "Draft preview cleared. Recreate it after you finish editing.",
    previewEmptyTitle: "Your draft preview will appear here",
    previewEmpty: "Add a product name and photo to make a sample ad.",
    mockBadge: "Mock preview",
    localOnly: "Local-only preview. No files are uploaded, no provider is called, and nothing is published.",
    maskRestoring: "Restoring saved product-ad draft…",
    previewArtworkLabel: "Local product-photo preview",
    captionWithBenefit: (benefit, productName) => `${benefit} — discover ${productName} today.`,
    captionWithoutBenefit: (productName) => `Meet ${productName}—made to fit your day.`,
  },
  ar: {
    eyebrow: "إعلان لمنتج",
    title: "ابدأ بمنتجك.",
    intro: "أضف صورة للمنتج وبعض الكلمات. ينشئ هذا العرض التجريبي معاينة محلية فقط؛ لا يتم رفع شيء ولا إرسال بيانات إلى مزود ذكاء اصطناعي.",
    productName: "اسم المنتج",
    productNamePlaceholder: "مثال: كريم اليدين سيدر آند سيلك",
    benefit: "شيء واحد تريد إبرازه (اختياري)",
    benefitPlaceholder: "ميزة مهمة أو عرض أو سبب لاختياره",
    productPhoto: "صورة المنتج (مطلوبة)",
    photoHelp: "اختر صورة واحدة بصيغة JPEG أو PNG أو WebP، وبحجم يصل إلى 10 MB.",
    brandLogo: "شعار العلامة التجارية (اختياري)",
    logoHelp: "أضف شعارًا إن توفر؛ يبقى محليًا في هذه المعاينة.",
    channel: "قناة الإعلان",
    format: "تنسيق الإعلان",
    channels: { instagram: "Instagram", facebook: "Facebook", tiktok: "TikTok" },
    formats: { square: "منشور الموجز · 1:1", story: "قصة · 9:16", landscape: "أفقي · 16:9" },
    createPreview: "إنشاء معاينة تجريبية",
    fileError: "اختر ملف صورة لا يتجاوز حجمه 10 MB.",
    previewTitle: "معاينة المسودة",
    previewReady: (channel, format) => `معاينة المسودة جاهزة لـ${channel} بصيغة ${format}.`,
    previewCleared: "تم مسح معاينة المسودة. أنشئها مرة أخرى بعد الانتهاء من التعديل.",
    previewEmptyTitle: "ستظهر معاينة المسودة هنا",
    previewEmpty: "أضف اسم المنتج وصورته لإنشاء إعلان تجريبي.",
    mockBadge: "معاينة تجريبية",
    localOnly: "معاينة محلية فقط. لا تُرفع ملفات، ولا يتم الاتصال بمزود، ولا يُنشر أي شيء.",
    maskRestoring: "جارٍ استعادة مسودة الإعلان…",
    previewArtworkLabel: "معاينة محلية لصورة المنتج",
    captionWithBenefit: (benefit, productName) => `${benefit} — اكتشف ${productName} اليوم.`,
    captionWithoutBenefit: (productName) => `تعرّف إلى ${productName}، المصمم ليومك.`,
  },
  ku: {
    eyebrow: "ڕیکلامی بەرهەم",
    title: "لە بەرهەمەکەتەوە دەست پێ بکە.",
    intro: "وێنەیەکی بەرهەم و چەند وشەیەک زیاد بکە. ئەم دیمۆیە تەنها پێشبینینی ناوخۆیی دروست دەکات؛ هیچ شتێک بار ناکرێت و بۆ دابینکەری زیرەکی دەستکرد نانێردرێت.",
    productName: "ناوی بەرهەم",
    productNamePlaceholder: "بۆ نموونە: کرێمی دەستی Cedar & Silk",
    benefit: "شتێک کە دەتەوێت جەختی لەسەر بکەیتەوە (ئارەزوومەندانە)",
    benefitPlaceholder: "سوودێکی سەرەکی، ئۆفەر، یان هۆکارێک بۆ هەڵبژاردنی",
    productPhoto: "وێنەی بەرهەم (پێویستە)",
    photoHelp: "یەک وێنەی بەرهەم هەڵبژێرە. JPEG، PNG، یان WebP، تا 10 MB.",
    brandLogo: "لۆگۆی براند (ئارەزوومەندانە)",
    logoHelp: "ئەگەر لۆگۆت هەیە زیاد بکە؛ لەم پێشبینینەدا ناوخۆیی دەمێنێتەوە.",
    channel: "کەناڵی دەرچوون",
    format: "فۆرماتی ڕیکلام",
    channels: { instagram: "Instagram", facebook: "Facebook", tiktok: "TikTok" },
    formats: { square: "پۆستی فید · 1:1", story: "ستۆری · 9:16", landscape: "ئاسۆیی · 16:9" },
    createPreview: "پێشبینینی نموونەیی دروست بکە",
    fileError: "فایلی وێنەیەک هەڵبژێرە کە قەبارەکەی لە 10 MB زیاتر نەبێت.",
    previewTitle: "پێشبینینی ڕەشنووس",
    previewReady: (channel, format) => `پێشبینینی ڕەشنووس ئامادەیە بۆ ${channel} بە ${format}.`,
    previewCleared: "پێشبینینی ڕەشنووس سڕایەوە. کاتێک دەستکاریت تەواو کرد دووبارە دروستی بکە.",
    previewEmptyTitle: "پێشبینینی ڕەشنووسەکەت لێرە دەردەکەوێت",
    previewEmpty: "ناوی بەرهەم و وێنەکەی زیاد بکە بۆ دروستکردنی ڕیکلامێکی نموونەیی.",
    mockBadge: "پێشبینینی نموونەیی",
    localOnly: "تەنها پێشبینینی ناوخۆییە. هیچ فایلێک بار ناکرێت، پەیوەندی بە دابینکەرەوە ناکرێت و هیچ شتێک بڵاو ناکرێتەوە.",
    maskRestoring: "ڕەشنووسی ڕیکلامی بەرهەم ڕێک دەکرێتەوە…",
    previewArtworkLabel: "پێشبینینی ناوخۆیی وێنەی بەرهەم",
    captionWithBenefit: (benefit, productName) => `${benefit} — ${productName} بۆ ئەمڕۆت بدۆزەرەوە.`,
    captionWithoutBenefit: (productName) => `${productName} بناسە؛ بۆ ڕۆژەکەت دروستکراوە.`,
  },
};

/** Scope transitions must mask stale content, including a transition to sign-out. */
export function shouldMaskProductAdDraft(key: string | null, hydratedKey: string | null): boolean {
  return hydratedKey !== key;
}

export function isSupportedProductAdImage(file: Pick<File, "type" | "size">): boolean {
  // Match the formats promised in the UI; image/* also admits SVG and GIF.
  // Keep validation local and refuse empty or oversized files.
  return ["image/jpeg", "image/png", "image/webp"].includes(file.type)
    && Number.isFinite(file.size) && file.size > 0 && file.size <= MAX_PRODUCT_AD_IMAGE_BYTES;
}

export function buildProductAdMockPreview(input: ProductAdDraftInput, locale: Locale): ProductAdMockPreview {
  const copy = copyByLocale[locale];
  const productName = input.productName.trim();
  const benefit = input.benefit.trim();

  return {
    headline: productName,
    caption: benefit ? copy.captionWithBenefit(benefit, productName) : copy.captionWithoutBenefit(productName),
    channel: copy.channels[input.channel],
    format: copy.formats[input.format],
  };
}

export function buildProductAdPreviewAnnouncement(
  status: ProductAdPreviewStatus,
  locale: Locale,
  output: Readonly<{ channel: ProductAdChannel; format: ProductAdFormat }> = defaultProductAdOutput,
): string {
  const copy = copyByLocale[locale];
  if (status === "ready") return copy.previewReady(copy.channels[output.channel], copy.formats[output.format]);
  if (status === "cleared") return copy.previewCleared;
  return "";
}

/**
 * The ids that describe one product-ad upload control, in reading order.
 *
 * `aria-invalid` alone tells a screen reader that a picked file is wrong without
 * saying why, and an expired draft upload leaves the required control empty with
 * no hint that the file the user chose earlier is gone. Both facts belong to the
 * control, so each is referenced only while its element is actually rendered -
 * a described-by list that points at a missing id is worse than a shorter one.
 */
export function productAdFileDescriptionIds(
  id: string,
  field: "photo" | "logo",
  flags: Readonly<{ noticeKind: ProductAdDraftAssetNoticeKind; error: boolean }> = { noticeKind: "none", error: false },
): string {
  const ids = [`${id}-${field}-help`];
  const noticeRelevant = field === "photo" ? flags.noticeKind !== "none" : flags.noticeKind === "photoLogo";
  if (noticeRelevant) ids.push(`${id}-draft-notice`);
  if (flags.error) ids.push(`${id}-${field}-error`);
  return ids.join(" ");
}
export function ProductAdInputWizard({ locale = "en", principal = null }: Readonly<{ locale?: Locale; principal?: ProductAdDraftPrincipal | null }>) {
  const id = useId();
  const copy = copyByLocale[locale];
  const [productName, setProductName] = useState("");
  const [benefit, setBenefit] = useState("");
  const [channel, setChannel] = useState<ProductAdChannel>(defaultProductAdOutput.channel);
  const [format, setFormat] = useState<ProductAdFormat>(defaultProductAdOutput.format);
  const [productPhoto, setProductPhoto] = useState<File | null>(null);
  const [brandLogo, setBrandLogo] = useState<File | null>(null);
  const [productPhotoUrl, setProductPhotoUrl] = useState<string | null>(null);
  const [brandLogoUrl, setBrandLogoUrl] = useState<string | null>(null);
  const [photoError, setPhotoError] = useState("");
  const [logoError, setLogoError] = useState("");
  const [preview, setPreview] = useState<ProductAdMockPreview | null>(null);
  const [previewStatus, setPreviewStatus] = useState<ProductAdPreviewStatus>("idle");
  const [restoredDraft, setRestoredDraft] = useState<ProductAdDraftState | null>(null);
  const [restoredProductPhoto, setRestoredProductPhoto] = useState<ProductAdDraftAsset | null>(null);
  const [restoredBrandLogo, setRestoredBrandLogo] = useState<ProductAdDraftAsset | null>(null);
  const [hydratedKey, setHydratedKey] = useState<string | null>(null);
  const userId = principal?.userId ?? null;
  const workspaceId = principal?.workspaceId ?? null;
  const key = productAdDraftStorageKey({ userId, workspaceId });

  function invalidatePreview() {
    setPreviewStatus((current) => nextProductAdPreviewStatus(current, "edited"));
    setPreview(null);
  }

  // URLs are allocated only in user file-picker events; these effects solely
  // revoke abandoned URLs. No synchronous setState inside an effect, and no
  // object URL is ever serialized into workspace-scoped draft storage.
  useEffect(() => () => {
    if (productPhotoUrl) URL.revokeObjectURL(productPhotoUrl);
  }, [productPhotoUrl]);

  useEffect(() => () => {
    if (brandLogoUrl) URL.revokeObjectURL(brandLogoUrl);
  }, [brandLogoUrl]);

  // A draft is keyed to both the authenticated user and workspace. Never read it
  // during SSR and never display the previous account's form while rehydrating.
  useEffect(() => {
    // Browser-only session restore runs after the committed render, never
    // synchronously inside the effect. Cancel any queued restore when the
    // authenticated principal/workspace changes or the wizard unmounts.
    let cancelled = false;
    queueMicrotask(() => {
      if (cancelled) return;
      let restored: ProductAdDraftState | null = null;
      if (key) {
        try { restored = restoreProductAdDraft(window.sessionStorage, { userId, workspaceId }); }
        catch { /* Storage can be blocked; the wizard still works in memory. */ }
      }
      if (cancelled) return;
      setProductName(restored?.productName ?? "");
      setBenefit(restored?.benefit ?? "");
      setChannel(restored?.channel ?? defaultProductAdOutput.channel);
      setFormat(restored?.format ?? defaultProductAdOutput.format);
      setProductPhoto(null);
      setBrandLogo(null);
      setProductPhotoUrl(null);
      setBrandLogoUrl(null);
      setRestoredProductPhoto(restored?.productPhoto ?? null);
      setRestoredBrandLogo(restored?.brandLogo ?? null);
      setRestoredDraft(restored);
      setPreview(null);
      setPreviewStatus("idle");
      setPhotoError("");
      setLogoError("");
      setHydratedKey(key);
    });
    return () => { cancelled = true; };
  }, [key, userId, workspaceId]);

  // Store descriptors and form settings, never image bytes. On reload, filenames
  // are remembered but actual file inputs remain empty and must be reattached.
  useEffect(() => {
    if (!key || hydratedKey !== key) return;
    try {
      const draft = applyProductAdDraftEdit(blankProductAdDraft(), {
        productName, benefit, channel, format,
        productPhoto: productPhoto ?? (restoredProductPhoto
          ? { name: restoredProductPhoto.name, type: restoredProductPhoto.mime, size: restoredProductPhoto.bytes }
          : null),
        brandLogo: brandLogo ?? (restoredBrandLogo
          ? { name: restoredBrandLogo.name, type: restoredBrandLogo.mime, size: restoredBrandLogo.bytes }
          : null),
      }, Date.now());
      if (hasProductAdDraftContent(draft)) {
        persistProductAdDraft(window.sessionStorage, { userId, workspaceId }, draft);
      } else {
        clearProductAdDraft(window.sessionStorage, { userId, workspaceId });
      }
    } catch { /* Session storage failure cannot affect the in-memory preview. */ }
  }, [key, hydratedKey, userId, workspaceId, productName, benefit, channel, format,
      productPhoto, brandLogo, restoredProductPhoto, restoredBrandLogo]);

  function updateProductPhoto(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0] ?? null;
    invalidatePreview();
    setRestoredProductPhoto(null);
    if (file && !isSupportedProductAdImage(file)) {
      setProductPhoto(null);
      setProductPhotoUrl(null);
      setPhotoError(copy.fileError);
      event.currentTarget.value = "";
      return;
    }
    setPhotoError("");
    setProductPhoto(file);
    setProductPhotoUrl(file ? URL.createObjectURL(file) : null);
  }

  function updateBrandLogo(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0] ?? null;
    invalidatePreview();
    setRestoredBrandLogo(null);
    if (file && !isSupportedProductAdImage(file)) {
      setBrandLogo(null);
      setBrandLogoUrl(null);
      setLogoError(copy.fileError);
      event.currentTarget.value = "";
      return;
    }
    setLogoError("");
    setBrandLogo(file);
    setBrandLogoUrl(file ? URL.createObjectURL(file) : null);
  }

  function createPreview(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPreview(buildProductAdMockPreview({ productName, benefit, channel, format }, locale));
    setPreviewStatus("ready");
  }

  // A reload keeps the stored names but never the image bytes, so "attached"
  // means "picked again in this session" and drives both the notice wording and
  // which upload control the notice belongs to.
  const masked = shouldMaskProductAdDraft(key, hydratedKey);
  const attachedAssets: ProductAdAttachedAssets = { productPhoto: productPhoto !== null, brandLogo: brandLogo !== null };
  const pendingAssetKind = productAdPendingAssetNoticeKind(restoredDraft, attachedAssets);
  // The outgoing scope's restored draft may still be in state while a new scope is
  // being hydrated, so the shared region is mounted silent during the mask. It is
  // the same paragraph the post-commit restore fills, which is the only way its
  // text is a change to an existing live region rather than inserted content.
  const draftNotice = masked ? "" : buildProductAdDraftRestoredNotice(restoredDraft, locale, attachedAssets);

  // The header is identical on both sides of the mask so React keeps this exact
  // <p> mounted across the scope change: assistive technology announces a text
  // change in a live region it already owns, not a region born holding its text.
  const wizardHeader = (
    <header className={styles.header}>
      <p className={styles.eyebrow}>{copy.eyebrow}</p>
      <h1 id={`${id}-title`}>{copy.title}</h1>
      <p className={styles.intro}>{copy.intro}</p>
      {/*
       * The region is mounted empty on the first commit and filled only when the
       * session-restored draft arrives. A live region that is inserted together
       * with its text is not announced, so the conditional form of this paragraph
       * silently swallowed the "the photo is gone, pick it again" instruction from
       * exactly the users who cannot see the header it sat in. The masked commit
       * below renders this same header for the same reason.
       */}
      <p id={`${id}-draft-notice`} className={styles.draftNotice} role="status" aria-live="polite" aria-atomic="true">{draftNotice}</p>
    </header>
  );

  // Guard transitions *to* anonymous as well as between authenticated scopes.
  // Until the post-commit reset completes, never render the prior account draft.
  // Only the static header and its empty region survive the mask, so no outgoing
  // scope's product name, brand-logo descriptor or preview can be read from them;
  // the mask stays an early return of its own tree because the user-flow contract
  // test pins that exact form.
  if (shouldMaskProductAdDraft(key, hydratedKey)) return (
    <section className={styles.shell} lang={localeTag(locale)} dir={localeDirection(locale)} aria-labelledby={`${id}-title`}>
      <div className={styles.formPanel}>
        {wizardHeader}
        <p role="status">{copy.maskRestoring}</p>
      </div>
    </section>
  );

  return (
    <section className={styles.shell} lang={localeTag(locale)} dir={localeDirection(locale)} aria-labelledby={`${id}-title`}>
      <div className={styles.formPanel}>
        {wizardHeader}

        <form className={styles.form} onSubmit={createPreview}>
          <label className={styles.field} htmlFor={`${id}-product-name`}>
            <span className={styles.labelText}>{copy.productName}<span className={styles.requiredMark} aria-hidden="true"> *</span></span>
            <input
              id={`${id}-product-name`}
              type="text"
              value={productName}
              onChange={(event) => { setProductName(event.target.value); invalidatePreview(); }}
              placeholder={copy.productNamePlaceholder}
              autoComplete="off"
              maxLength={120}
              dir="auto"
              required
            />
          </label>

          <label className={styles.field} htmlFor={`${id}-benefit`}>
            <span className={styles.labelText}>{copy.benefit}</span>
            <textarea
              id={`${id}-benefit`}
              value={benefit}
              onChange={(event) => { setBenefit(event.target.value); invalidatePreview(); }}
              placeholder={copy.benefitPlaceholder}
              rows={3}
              maxLength={280}
              dir="auto"
            />
          </label>

          <div className={styles.field}>
            <label className={styles.labelText} htmlFor={`${id}-product-photo`}>
              {copy.productPhoto}<span className={styles.requiredMark} aria-hidden="true"> *</span>
            </label>
            <input
              id={`${id}-product-photo`}
              className={styles.fileInput}
              type="file"
              accept="image/jpeg,image/png,image/webp"
              aria-describedby={productAdFileDescriptionIds(id, "photo", { noticeKind: pendingAssetKind, error: photoError.length > 0 })}
              aria-invalid={photoError ? true : undefined}
              required
              onChange={updateProductPhoto}
            />
            <small id={`${id}-photo-help`} className={styles.helpText}>{copy.photoHelp}</small>
            {productPhoto && <small className={styles.fileName} dir="auto">{productPhoto.name}</small>}
            {photoError && <small id={`${id}-photo-error`} className={styles.errorText} role="alert">{photoError}</small>}
          </div>

          <div className={styles.field}>
            <label className={styles.labelText} htmlFor={`${id}-brand-logo`}>{copy.brandLogo}</label>
            <input
              id={`${id}-brand-logo`}
              className={styles.fileInput}
              type="file"
              accept="image/jpeg,image/png,image/webp"
              aria-describedby={productAdFileDescriptionIds(id, "logo", { noticeKind: pendingAssetKind, error: logoError.length > 0 })}
              aria-invalid={logoError ? true : undefined}
              onChange={updateBrandLogo}
            />
            <small id={`${id}-logo-help`} className={styles.helpText}>{copy.logoHelp}</small>
            {brandLogo && <small className={styles.fileName} dir="auto">{brandLogo.name}</small>}
            {logoError && <small id={`${id}-logo-error`} className={styles.errorText} role="alert">{logoError}</small>}
          </div>

          <div className={styles.outputFields}>
            <label className={styles.field} htmlFor={`${id}-channel`}>
              <span className={styles.labelText}>{copy.channel}</span>
              <select id={`${id}-channel`} value={channel} onChange={(event) => { setChannel(event.target.value as ProductAdChannel); invalidatePreview(); }}>
                <option value="instagram">{copy.channels.instagram}</option>
                <option value="facebook">{copy.channels.facebook}</option>
                <option value="tiktok">{copy.channels.tiktok}</option>
              </select>
            </label>
            <label className={styles.field} htmlFor={`${id}-format`}>
              <span className={styles.labelText}>{copy.format}</span>
              <select id={`${id}-format`} value={format} onChange={(event) => { setFormat(event.target.value as ProductAdFormat); invalidatePreview(); }}>
                <option value="square">{copy.formats.square}</option>
                <option value="story">{copy.formats.story}</option>
                <option value="landscape">{copy.formats.landscape}</option>
              </select>
            </label>
          </div>

          <button className={styles.submitButton} type="submit">{copy.createPreview}</button>
        </form>
      </div>

      <aside className={styles.previewPanel} aria-labelledby={`${id}-preview-title`}>
        <p className="sr-only" role="status" aria-live="polite" aria-atomic="true">
          {buildProductAdPreviewAnnouncement(previewStatus, locale, { channel, format })}
        </p>
        <div className={styles.previewHeading}>
          <div>
            <p className={styles.eyebrow}>{copy.mockBadge}</p>
            <h2 id={`${id}-preview-title`}>{preview ? copy.previewTitle : copy.previewEmptyTitle}</h2>
          </div>
          <span className={styles.previewMark} aria-hidden="true">Aa</span>
        </div>
        {preview ? (
          <div className={styles.previewCard}>
            <div className={styles.previewMeta}>
              <span>{preview.channel}</span>
              <span>{preview.format}</span>
            </div>
            {productPhoto && productPhotoUrl && (
              <div
                className={`${styles.previewArtwork} ${format === "story" ? styles.previewStory : format === "landscape" ? styles.previewLandscape : styles.previewSquare}`}
                aria-label={copy.previewArtworkLabel}
              >
                {/* File data is only read via revocable browser object URLs. */}
                {/* eslint-disable-next-line @next/next/no-img-element */}
                <img
                  data-preview-asset="product"
                  className={styles.previewProductImage}
                  src={productPhotoUrl}
                  alt={productName.trim()}
                />
                {brandLogo && brandLogoUrl && (
                  /* eslint-disable-next-line @next/next/no-img-element */
                  <img
                    data-preview-asset="brand-logo"
                    className={styles.previewBrandLogo}
                    src={brandLogoUrl}
                    alt={copy.brandLogo}
                  />
                )}
              </div>
            )}
            <h3 dir="auto">{preview.headline}</h3>
            <p dir="auto">{preview.caption}</p>
            {productPhoto && <small className={styles.previewAsset} dir="auto">{copy.productPhoto}: {productPhoto.name}</small>}
            {brandLogo && <small className={styles.previewAsset} dir="auto">{copy.brandLogo}: {brandLogo.name}</small>}
          </div>
        ) : (
          <p className={styles.emptyPreview}>{copy.previewEmpty}</p>
        )}
        <p className={styles.localNotice}>{copy.localOnly}</p>
      </aside>
    </section>
  );
}
