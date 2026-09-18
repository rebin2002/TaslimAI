"use client";

import Link from "next/link";
import { ArrowUpLeft } from "lucide-react";
import { featureRoute, type Department } from "@/lib/data";
import { useLocale } from "@/components/LocaleProvider";

export function DepartmentSection({ department, index }: Readonly<{ department: Department; index: number }>) {
  const { t } = useLocale();
  const DepartmentIcon = department.icon;

  return (
    <section className={`department-section department-${department.color}`} aria-labelledby={`${department.id}-title`}>
      <div className="section-heading">
        <div className="section-heading-main">
          <span className="section-index">0{index + 1}</span>
          <span className="department-icon"><DepartmentIcon size={18} /></span>
          <div>
            <p className="section-eyebrow">{t(department.eyebrowKey)}</p>
            <h2 id={`${department.id}-title`}>{t(department.titleKey)}</h2>
          </div>
        </div>
        <Link href={`/${department.id}`} className="section-link">
          {t("home.viewAll")} <ArrowUpLeft size={15} />
        </Link>
      </div>
      <div className="feature-row" tabIndex={0} aria-label={t(department.titleKey)}>
        {department.features.map((feature) => {
          const FeatureIcon = feature.icon;
          return (
            <Link key={feature.id} href={featureRoute(department.id, feature.id)} className={`feature-card tone-${feature.tone}`}>
              <span className="feature-card-icon"><FeatureIcon size={19} strokeWidth={1.8} /></span>
              <span className="feature-card-label">{t(feature.labelKey)}</span>
              <span className="feature-card-arrow"><ArrowUpLeft size={15} /></span>
              <span className="feature-card-glow" />
            </Link>
          );
        })}
      </div>
    </section>
  );
}
