"use client";

import Link from "next/link";
import { useEffect } from "react";
import { RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";

export default function ResearchStudioError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const { t } = useLocale();
  useEffect(() => { console.error("Research Studio route recovery", "route_error"); }, []);
  return <main className="placeholder-page"><section className="placeholder-panel" role="alert"><RefreshCw className="placeholder-symbol" size={28} /><p className="section-eyebrow">{t("research.title")}</p><h1>{t("error.title")}</h1><p>{t("error.description")}</p><div className="placeholder-actions"><button className="primary-button" onClick={reset}>{t("error.retry")}</button><Link className="secondary-button" href="/assets">{t("error.openAssets")}</Link></div></section></main>;
}
