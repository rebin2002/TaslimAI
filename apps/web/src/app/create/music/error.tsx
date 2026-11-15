"use client";

import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useEffect } from "react";
import { useLocale } from "@/components/LocaleProvider";

export function MusicStudioRecoveryView({ reset, t }: Readonly<{ reset: () => void; t: (key: string) => string }>) {
  return (
    <main className="placeholder-page">
      <section className="placeholder-panel" role="alert" aria-live="assertive">
        <RefreshCw className="placeholder-symbol" size={28} aria-hidden="true" />
        <p className="section-eyebrow">{t("music.title")}</p>
        <h1>{t("error.title")}</h1>
        <p>{t("error.description")}</p>
        <div className="placeholder-actions">
          <button className="primary-button" type="button" onClick={reset}>
            <RefreshCw size={15} aria-hidden="true" /> {t("error.retry")}
          </button>
          <Link className="secondary-button" href="/assets">{t("error.openAssets")}</Link>
        </div>
      </section>
    </main>
  );
}

export default function MusicStudioError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const { t } = useLocale();

  useEffect(() => {
    console.error("Music Studio route recovery", "route_error");
  }, []);

  return <MusicStudioRecoveryView reset={reset} t={t} />;
}
