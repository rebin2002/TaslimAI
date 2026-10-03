"use client";

import { useEffect } from "react";
import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";

export default function PresentationStudioError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const { t } = useLocale();
  useEffect(() => { console.error("Presentation Studio route recovery", "route_error"); }, []);
  return <main className="placeholder-page"><section className="placeholder-panel" role="alert"><RefreshCw className="placeholder-symbol" size={28} /><p className="section-eyebrow">{t("presentation.title")}</p><h1>{t("error.title")}</h1><p>{t("error.description")}</p><div className="placeholder-actions"><button className="primary-button" onClick={reset}>{t("error.retry")}</button><Link className="secondary-button" href="/assets">{t("error.openAssets")}</Link></div></section></main>;
}
