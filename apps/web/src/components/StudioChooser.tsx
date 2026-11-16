"use client";
import Link from "next/link";
import { ArrowUpRight, HeartPulse, MessageSquare, Package } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { healthCareCopy } from "@/lib/healthCareI18n";
import { studioCategories, type StudioCategory, type StudioItem } from "@/lib/navigation";

/**
 * This lane owns chooser visibility only. The tile is navigation-only and points
 * at the `/create/health` preview owned by the Health route lane (#715); it takes
 * no medical input, stores nothing, and starts no provider or paid call.
 */
export const healthCareTileHref = "/create/health";
export const productAdTileHref = "/create/product-ad";

/** The chooser counts its two feature-specific entries beyond the base studio catalogue. */
const extraTileCount = 2;

export function StudioChooser({ compact = false }: Readonly<{ compact?: boolean }>) {
  if (compact) return <CompactStudioLauncher />;
  return <FullStudioChooser />;
}

function CompactStudioLauncher() {
  const { t } = useLocale();
  const studios = studioCategories.flatMap((category) => category.studios);
  return (
    <section className="studio-chooser studio-chooser-compact" aria-labelledby="studio-chooser-title">
      <div className="studio-chooser-heading">
        <div><p className="studio-section-kicker">{t("home.studiosKicker")}</p><h2 id="studio-chooser-title">{t("home.studiosTitle")}</h2></div>
        <Link href="/create" className="studio-view-all">{t("home.viewAll")} <ArrowUpRight size={14} /></Link>
      </div>
      <div className="studio-launcher-grid">{studios.map((studio) => <StudioTile key={studio.href} studio={studio} />)}<ProductAdTile /><HealthCareTile /></div>
    </section>
  );
}

function FullStudioChooser() {
  const { t, locale } = useLocale();
  const copy = healthCareCopy(locale);
  const studioCount = studioCategories.reduce(
    (count, category) => count + category.studios.length,
    extraTileCount,
  );
  return (
    <section className="studio-chooser" aria-labelledby="studio-chooser-title">
      <div className="studio-chooser-heading"><div><p className="section-eyebrow">{t("create.eyebrow")}</p><h1 id="studio-chooser-title">{t("create.title")}</h1><p>{t("create.subtitle")}</p></div><span className="studio-chooser-count">{studioCount}</span></div>
      <div className="studio-category-list">
        {studioCategories.map((category) => <StudioCategorySection key={category.id} category={category} />)}
        <section className="studio-category" aria-labelledby="studio-category-product-ad">
          <h3 id="studio-category-product-ad">{productAdTileCopy(locale).category}</h3>
          <div className="studio-card-grid"><ProductAdTile /></div>
        </section>
        <section className="studio-category" aria-labelledby="studio-category-health-care" data-health-care-category="true">
          <h3 id="studio-category-health-care">{copy.category}</h3>
          <div className="studio-card-grid"><HealthCareTile /></div>
        </section>
      </div>
      <Link href="/chat" className="studio-chat-link"><span className="studio-chat-icon"><MessageSquare size={17} /></span><span><strong>{t("create.openChat")}</strong><small>{t("create.openChatHint")}</small></span><ArrowUpRight size={17} /></Link>
    </section>
  );
}

function StudioCategorySection({ category }: Readonly<{ category: StudioCategory }>) {
  const { t } = useLocale();
  return <section className="studio-category" aria-labelledby={`studio-category-${category.id}`}><h3 id={`studio-category-${category.id}`}>{t(category.labelKey)}</h3><div className="studio-card-grid">{category.studios.map((studio) => <StudioTile key={studio.href} studio={studio} />)}</div></section>;
}

function StudioTile({ studio }: Readonly<{ studio: StudioItem }>) {
  const { t } = useLocale();
  const Icon = studio.icon;
  return <Link href={studio.href} className="studio-card"><span className="studio-card-icon"><Icon size={18} strokeWidth={1.9} /></span><span className="studio-card-copy"><strong>{t(studio.labelKey)}</strong><small>{t(studio.descriptionKey)}</small></span><ArrowUpRight className="studio-card-arrow" size={13} /></Link>;
}

/**
 * Reuses the Health route lane's feature-local EN/AR/CKB copy so the tile can
 * never drift from the landing page it links to, and never appears untranslated.
 */
function HealthCareTile() {
  const { locale } = useLocale();
  const copy = healthCareCopy(locale);
  return <Link href={healthCareTileHref} className="studio-card" data-health-care-tile="true"><span className="studio-card-icon"><HeartPulse size={18} strokeWidth={1.9} /></span><span className="studio-card-copy"><strong>{copy.title}</strong><small>{copy.status}</small></span><ArrowUpRight className="studio-card-arrow" size={13} /></Link>;
}

/** Product Ad is a local-only draft preview. Do not imply AI generation or publishing. */
const productAdTileStrings = {
  en: { category: "Product advertising", title: "Product Ad", hint: "Create a local draft preview with your product photo and logo" },
  ar: { category: "إعلانات المنتجات", title: "إعلان منتج", hint: "أنشئ معاينة تجريبية محلية بصورة المنتج والشعار" },
  ku: { category: "ڕیکلامی بەرهەم", title: "ڕیکلامی بەرهەم", hint: "پێشبینینی ڕەشنووس بە وێنەی بەرهەم و لۆگۆ دروست بکە" },
} as const;

export function productAdTileCopy(locale: keyof typeof productAdTileStrings) {
  return productAdTileStrings[locale];
}

function ProductAdTile() {
  const { locale } = useLocale();
  const copy = productAdTileCopy(locale);
  return <Link href={productAdTileHref} className="studio-card" data-product-ad-tile="true"><span className="studio-card-icon"><Package size={18} strokeWidth={1.9} /></span><span className="studio-card-copy"><strong>{copy.title}</strong><small>{copy.hint}</small></span><ArrowUpRight className="studio-card-arrow" size={13} /></Link>;
}
