"use client";

import Link from "next/link";
import { useEffect } from "react";
import { RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";

export default function SocialError({ reset }: { reset: () => void }) {
  const { t } = useLocale();
  useEffect(() => { console.error("Social Studio route recovered safely."); }, []);
  return <div className="error-page"><div className="error-card" role="alert" aria-live="assertive"><RefreshCw size={28} /><p className="section-eyebrow">{t("social.eyebrow")}</p><h1>{t("social.routeError")}</h1><p>{t("social.routeErrorHint")}</p><div className="error-actions"><button className="primary-button" onClick={reset}>{t("social.retry")}</button><Link className="secondary-button" href="/">{t("placeholder.backHome")}</Link></div></div></div>;
}
