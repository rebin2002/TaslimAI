"use client";
import Link from "next/link";
import { ArrowUpRight, HeartPulse, MessageSquare } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { studioCategories, type StudioCategory, type StudioItem } from "@/lib/navigation";

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
      <div className="studio-launcher-grid">{studios.map((studio) => <StudioTile key={studio.href} studio={studio} />)}<HealthCareTile /></div>
    </section>
  );
}

function FullStudioChooser() {
  const { t, locale } = useLocale();
  return (
    <section className="studio-chooser" aria-labelledby="studio-chooser-title">
      <div className="studio-chooser-heading"><div><p className="section-eyebrow">{t("create.eyebrow")}</p><h2 id="studio-chooser-title">{t("create.title")}</h2><p>{t("create.subtitle")}</p></div><span className="studio-chooser-count">{studioCategories.reduce((count, category) => count + category.studios.length, 1)}</span></div>
      <div className="studio-category-list">{studioCategories.map((category) => <StudioCategorySection key={category.id} category={category} />)}<section className="studio-category" aria-labelledby="studio-category-health-care"><h3 id="studio-category-health-care">{healthCareCopy[locale].category}</h3><div className="studio-card-grid"><HealthCareTile /></div></section></div>
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

// Localized here to avoid changing shared i18n and navigation owner paths.
// Health Care is currently informational only: it accepts no medical data.
const healthCareCopy = {
  en: { category: "Health & Wellness", title: "Health Care", hint: "Private health tools — coming soon" },
  ar: { category: "الصحة والعافية", title: "الرعاية الصحية", hint: "أدوات صحية خاصة — قريباً" },
  ku: { category: "تەندروستی و خۆشگوزەرانی", title: "چاودێری تەندروستی", hint: "ئامرازە تایبەتەکانی تەندروستی — بەم زووانە" },
} as const;

function HealthCareTile() {
  const { locale } = useLocale();
  const copy = healthCareCopy[locale];
  return <Link href="/create/health" className="studio-card"><span className="studio-card-icon"><HeartPulse size={18} strokeWidth={1.9} /></span><span className="studio-card-copy"><strong>{copy.title}</strong><small>{copy.hint}</small></span><ArrowUpRight className="studio-card-arrow" size={13} /></Link>;
}
