"use client";

import Link from "next/link";
import { ArrowLeft, ArrowUpRight, Code2 } from "lucide-react";
import { usePathname } from "next/navigation";
import { useLocale } from "@/components/LocaleProvider";
import { developmentNavigation } from "@/lib/developmentNavigation";
import { resolveDevelopmentHubCopy } from "@/lib/developmentHubCopy";
import styles from "./DevelopmentHub.module.css";

export function DevelopmentHub() {
  const pathname = usePathname();
  const { locale, t } = useLocale();
  const strings = resolveDevelopmentHubCopy(locale, t);

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
