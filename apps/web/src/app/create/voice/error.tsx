"use client";

import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useEffect } from "react";
import { useLocale } from "@/components/LocaleProvider";

export default function VoiceStudioError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const { t } = useLocale();

  useEffect(() => {
    console.error("Voice Studio route recovery", "route_error");
  }, []);

  return (
    <div className="placeholder-page">
      <section className="placeholder-panel" role="alert" aria-live="assertive">
        <RefreshCw className="placeholder-symbol" size={28} aria-hidden="true" />
        <p className="section-eyebrow">{t("voice.title")}</p>
        <h1>{t("error.title")}</h1>
        <p>{t("error.description")}</p>
        <div className="placeholder-actions">
          <button className="primary-button" type="button" onClick={reset}>
            <RefreshCw size={15} aria-hidden="true" /> {t("error.retry")}
          </button>
          <Link className="secondary-button" href="/assets">{t("error.openAssets")}</Link>
        </div>
      </section>
    </div>
  );
}
