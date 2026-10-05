"use client";

import Link from "next/link";
import { ArrowLeft, ArrowUpRight, Code2 } from "lucide-react";
import { usePathname } from "next/navigation";
import { useLocale } from "@/components/LocaleProvider";
import { developmentNavigation } from "@/lib/developmentNavigation";
import type { Locale } from "@/lib/i18n";
import styles from "./DevelopmentHub.module.css";

type HubCopy = {
  eyebrow: string;
  description: string;
  navigationLabel: string;
  open: string;
  backHome: string;
  footer: string;
};

const copy: Record<Locale, HubCopy> = {
  en: {
    eyebrow: "Development workspace",
    description: "Move from learning to shipping with a clear path through the development workspace.",
    navigationLabel: "Development tools",
    open: "Open",
    backHome: "Back to home",
    footer: "Choose one path at a time; your workspace progress stays with the feature you open.",
  },
  ar: {
    eyebrow: "مساحة عمل التطوير",
    description: "انتقل من التعلّم إلى الشحن عبر مسار واضح داخل مساحة التطوير.",
    navigationLabel: "أدوات التطوير",
    open: "فتح",
    backHome: "العودة إلى الرئيسية",
    footer: "اختر مساراً واحداً في كل مرة؛ يبقى تقدّم مساحة العمل مع الميزة التي تفتحها.",
  },
  ku: {
    eyebrow: "شوێنکاریی گەشەپێدان",
    description: "لە فێربوونەوە بۆ ناردن بڕۆ بە ڕێڕەوێکی ڕوون لە شوێنکاریی گەشەپێدان.",
    navigationLabel: "ئامرازەکانی گەشەپێدان",
    open: "کردنەوە",
    backHome: "گەڕانەوە بۆ سەرەکی",
    footer: "لە هەر جارێکدا یەک ڕێڕەو هەڵبژێرە؛ پێشکەوتنی شوێنکار لەگەڵ ئەو تایبەتمەندییە دەمێنێتەوە کە دەیکەیتەوە.",
  },
};

export function DevelopmentHub() {
  const pathname = usePathname();
  const { locale, t } = useLocale();
  const strings = copy[locale];

  return (
    <section className={styles.page} aria-labelledby="development-hub-title">
      <div className={styles.container}>
        <Link href="/" className={styles.backLink}>
          <ArrowLeft size={15} aria-hidden="true" />
          {strings.backHome}
        </Link>

        <header className={styles.header}>
          <div>
            <p className={styles.eyebrow}><Code2 size={14} aria-hidden="true" /> {strings.eyebrow}</p>
            <h1 id="development-hub-title">{t("department.development")}</h1>
            <p className={styles.description}>{strings.description}</p>
          </div>
          <span className={styles.headerMark} aria-hidden="true"><Code2 size={28} /></span>
        </header>

        <nav className={styles.navigation} aria-label={strings.navigationLabel}>
          <ol className={styles.grid}>
            {developmentNavigation.map((item) => {
              const Icon = item.icon;
              const active = pathname === item.href || pathname.startsWith(`${item.href}/`);
              return (
                <li key={item.id}>
                  <Link
                    href={item.href}
                    className={`${styles.card} ${styles[`tone-${item.tone}`]} ${active ? styles.active : ""}`}
                    aria-current={active ? "page" : undefined}
                    aria-label={`${t(item.labelKey)} — ${strings.open}`}
                  >
                    <span className={styles.icon}><Icon size={19} aria-hidden="true" /></span>
                    <span className={styles.cardCopy}>
                      <strong>{t(item.labelKey)}</strong>
                      <small>{strings.open}</small>
                    </span>
                    <ArrowUpRight className={styles.arrow} size={16} aria-hidden="true" />
                  </Link>
                </li>
              );
            })}
          </ol>
        </nav>

        <p className={styles.footer}>{strings.footer}</p>
      </div>
    </section>
  );
}
