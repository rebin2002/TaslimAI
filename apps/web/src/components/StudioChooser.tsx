"use client";

import Link from "next/link";
import { ArrowUpRight, MessageSquare } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { studioCategories, type StudioCategory } from "@/lib/navigation";

export function StudioChooser({ compact = false }: Readonly<{ compact?: boolean }>) {
  const { t } = useLocale();
  const eyebrowKey = compact ? "home.createEyebrow" : "create.eyebrow";
  const titleKey = compact ? "home.createPanelLabel" : "create.title";
  const subtitleKey = compact ? "home.createSubtitle" : "create.subtitle";

  return (
    <section className={`studio-chooser ${compact ? "studio-chooser-compact" : ""}`} aria-labelledby="studio-chooser-title">
      <div className="studio-chooser-heading">
        <div>
          <p className="section-eyebrow">{t(eyebrowKey)}</p>
          <h2 id="studio-chooser-title">{t(titleKey)}</h2>
          <p>{t(subtitleKey)}</p>
        </div>
        {!compact && <span className="studio-chooser-count">{studioCategories.reduce((count, category) => count + category.studios.length, 0)}</span>}
      </div>
      <div className="studio-category-list">
        {studioCategories.map((category) => <StudioCategorySection key={category.id} category={category} />)}
      </div>
      <Link href="/chat" className="studio-chat-link">
        <span className="studio-chat-icon"><MessageSquare size={17} /></span>
        <span><strong>{t("create.openChat")}</strong><small>{t("create.openChatHint")}</small></span>
        <ArrowUpRight size={17} />
      </Link>
    </section>
  );
}

function StudioCategorySection({ category }: Readonly<{ category: StudioCategory }>) {
  const { t } = useLocale();
  return (
    <section className="studio-category" aria-labelledby={`studio-category-${category.id}`}>
      <h3 id={`studio-category-${category.id}`}>{t(category.labelKey)}</h3>
      <div className="studio-card-grid">
        {category.studios.map((studio) => {
          const Icon = studio.icon;
          return (
            <Link key={studio.href} href={studio.href} className="studio-card">
              <span className="studio-card-icon"><Icon size={19} strokeWidth={1.8} /></span>
              <span className="studio-card-copy"><strong>{t(studio.labelKey)}</strong><small>{t(studio.descriptionKey)}</small></span>
              <ArrowUpRight className="studio-card-arrow" size={16} />
            </Link>
          );
        })}
      </div>
    </section>
  );
}
