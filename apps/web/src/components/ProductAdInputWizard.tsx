"use client";

import { useEffect, useId, useState, type ChangeEvent, type FormEvent } from "react";
import { applyProductAdDraftEdit, blankProductAdDraft, buildProductAdDraftRestoredNotice, clearProductAdDraft, hasProductAdDraftContent, persistProductAdDraft, productAdDraftStorageKey, restoreProductAdDraft, type ProductAdDraftAsset, type ProductAdDraftPrincipal, type ProductAdDraftState } from "@/lib/productAdDraftState";
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
  previewEmptyTitle: string;
  previewEmpty: string;
  mockBadge: string;
  localOnly: string;
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
    previewEmptyTitle: "Your draft preview will appear here",
    previewEmpty: "Add a product name and photo to make a sample ad.",
    mockBadge: "Mock preview",
    localOnly: "Local-only preview. No files are uploaded, no provider is called, and nothing is published.",
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
    previewEmptyTitle: "ستظهر معاينة المسودة هنا",
    previewEmpty: "أضف اسم المنتج وصورته لإنشاء إعلان تجريبي.",
    mockBadge: "معاينة تجريبية",
    localOnly: "معاينة محلية فقط. لا تُرفع ملفات، ولا يتم الاتصال بمزود، ولا يُنشر أي شيء.",
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
    previewEmptyTitle: "پێشبینینی ڕەشنووسەکەت لێرە دەردەکەوێت",
    previewEmpty: "ناوی بەرهەم و وێنەکەی زیاد بکە بۆ دروستکردنی ڕیکلامێکی نموونەیی.",
    mockBadge: "پێشبینینی نموونەیی",
    localOnly: "تەنها پێشبینینی ناوخۆییە. هیچ فایلێک بار ناکرێت، پەیوەندی بە دابینکەرەوە ناکرێت و هیچ شتێک بڵاو ناکرێتەوە.",
    captionWithBenefit: (benefit, productName) => `${benefit} — ${productName} بۆ ئەمڕۆت بدۆزەرەوە.`,
    captionWithoutBenefit: (productName) => `${productName} بناسە؛ بۆ ڕۆژەکەت دروستکراوە.`,
  },
};

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
  const [restoredDraft, setRestoredDraft] = useState<ProductAdDraftState | null>(null);
  const [restoredProductPhoto, setRestoredProductPhoto] = useState<ProductAdDraftAsset | null>(null);
  const [restoredBrandLogo, setRestoredBrandLogo] = useState<ProductAdDraftAsset | null>(null);
  const [hydratedKey, setHydratedKey] = useState<string | null>(null);
  const userId = principal?.userId ?? null;
  const workspaceId = principal?.workspaceId ?? null;
  const key = productAdDraftStorageKey({ userId, workspaceId });

  // Object URLs never leave this tab, are never persisted, and are revoked when
  // the file changes or the authenticated wizard unmounts. A restored filename
  // is not a file: users must reattach bytes before previewing the image.
  useEffect(() => {
    if (!productPhoto) {
      setProductPhotoUrl(null);
      return;
    }
    const url = URL.createObjectURL(productPhoto);
    setProductPhotoUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [productPhoto]);

  useEffect(() => {
    if (!brandLogo) {
      setBrandLogoUrl(null);
      return;
    }
    const url = URL.createObjectURL(brandLogo);
    setBrandLogoUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [brandLogo]);

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
      setRestoredProductPhoto(restored?.productPhoto ?? null);
      setRestoredBrandLogo(restored?.brandLogo ?? null);
      setRestoredDraft(restored);
      setPreview(null);
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
    setPreview(null);
    setRestoredProductPhoto(null);
    if (file && !isSupportedProductAdImage(file)) {
      setProductPhoto(null);
      setPhotoError(copy.fileError);
      event.currentTarget.value = "";
      return;
    }
    setPhotoError("");
    setProductPhoto(file);
  }

  function updateBrandLogo(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0] ?? null;
    setPreview(null);
    setRestoredBrandLogo(null);
    if (file && !isSupportedProductAdImage(file)) {
      setBrandLogo(null);
      setLogoError(copy.fileError);
      event.currentTarget.value = "";
      return;
    }
    setLogoError("");
    setBrandLogo(file);
  }

  function createPreview(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPreview(buildProductAdMockPreview({ productName, benefit, channel, format }, locale));
  }

  if (key && hydratedKey !== key) return <p role="status">Restoring saved product-ad draft…</p>;

  return (
    <section className={styles.shell} lang={localeTag(locale)} dir={localeDirection(locale)} aria-labelledby={`${id}-title`}>
      <div className={styles.formPanel}>
        <header className={styles.header}>
          <p className={styles.eyebrow}>{copy.eyebrow}</p>
          <h1 id={`${id}-title`}>{copy.title}</h1>
          <p className={styles.intro}>{copy.intro}</p>
          {restoredDraft && <p role="status" aria-live="polite">{buildProductAdDraftRestoredNotice(restoredDraft, locale)}</p>}
        </header>

        <form className={styles.form} onSubmit={createPreview}>
          <label className={styles.field} htmlFor={`${id}-product-name`}>
            <span className={styles.labelText}>{copy.productName}<span className={styles.requiredMark} aria-hidden="true"> *</span></span>
            <input
              id={`${id}-product-name`}
              type="text"
              value={productName}
              onChange={(event) => { setProductName(event.target.value); setPreview(null); }}
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
              onChange={(event) => { setBenefit(event.target.value); setPreview(null); }}
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
              aria-describedby={`${id}-photo-help`}
              aria-invalid={photoError ? true : undefined}
              required
              onChange={updateProductPhoto}
            />
            <small id={`${id}-photo-help`} className={styles.helpText}>{copy.photoHelp}</small>
            {productPhoto && <small className={styles.fileName} dir="auto">{productPhoto.name}</small>}
            {photoError && <small className={styles.errorText} role="alert">{photoError}</small>}
          </div>

          <div className={styles.field}>
            <label className={styles.labelText} htmlFor={`${id}-brand-logo`}>{copy.brandLogo}</label>
            <input
              id={`${id}-brand-logo`}
              className={styles.fileInput}
              type="file"
              accept="image/jpeg,image/png,image/webp"
              aria-describedby={`${id}-logo-help`}
              aria-invalid={logoError ? true : undefined}
              onChange={updateBrandLogo}
            />
            <small id={`${id}-logo-help`} className={styles.helpText}>{copy.logoHelp}</small>
            {brandLogo && <small className={styles.fileName} dir="auto">{brandLogo.name}</small>}
            {logoError && <small className={styles.errorText} role="alert">{logoError}</small>}
          </div>

          <div className={styles.outputFields}>
            <label className={styles.field} htmlFor={`${id}-channel`}>
              <span className={styles.labelText}>{copy.channel}</span>
              <select id={`${id}-channel`} value={channel} onChange={(event) => { setChannel(event.target.value as ProductAdChannel); setPreview(null); }}>
                <option value="instagram">{copy.channels.instagram}</option>
                <option value="facebook">{copy.channels.facebook}</option>
                <option value="tiktok">{copy.channels.tiktok}</option>
              </select>
            </label>
            <label className={styles.field} htmlFor={`${id}-format`}>
              <span className={styles.labelText}>{copy.format}</span>
              <select id={`${id}-format`} value={format} onChange={(event) => { setFormat(event.target.value as ProductAdFormat); setPreview(null); }}>
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
        <div className={styles.previewHeading}>
          <div>
            <p className={styles.eyebrow}>{copy.mockBadge}</p>
            <h2 id={`${id}-preview-title`}>{preview ? copy.previewTitle : copy.previewEmptyTitle}</h2>
          </div>
          <span className={styles.previewMark} aria-hidden="true">Aa</span>
        </div>
        {preview ? (
          <div className={styles.previewCard} aria-live="polite">
            <div className={styles.previewMeta}>
              <span>{preview.channel}</span>
              <span>{preview.format}</span>
            </div>
            {productPhoto && productPhotoUrl && (
              <div
                className={`${styles.previewArtwork} ${format === "story" ? styles.previewStory : format === "landscape" ? styles.previewLandscape : styles.previewSquare}`}
                aria-label="Local product-photo preview"
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
