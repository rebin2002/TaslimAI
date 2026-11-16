"use client";

import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";

export default function DocumentStudioError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const { t } = useLocale();
  return <div className="protected-page"><section className="account-card document-error-state" role="alert" aria-live="assertive"><p className="section-eyebrow">{t("document.resultEyebrow")}</p><h1>{t("document.completedLoadError")}</h1><p>{t("document.completedLoadHint")}</p><div className="document-result-actions"><button className="primary-button" type="button" onClick={reset}><RefreshCw size={15} /> {t("document.retry")}</button><Link className="secondary-button" href="/assets">{t("document.openAssets")}</Link></div></section></div>;
}
