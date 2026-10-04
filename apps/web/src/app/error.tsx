"use client";

import { useEffect } from "react";
import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";

/**
 * Catch render errors for every route that does not provide a more specific
 * boundary. Only the opaque Next digest is observed; error messages can carry
 * provider or user-content details and must not be rendered or logged.
 */
export default function AppRouteError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const { t } = useLocale();

  useEffect(() => {
    console.error("Taslim route recovery", error.digest ? "digest-present" : "digest-unavailable");
  }, [error.digest]);

  return (
    <main className="placeholder-page">
      <section className="placeholder-panel" role="alert" aria-live="assertive">
        <RefreshCw className="placeholder-symbol" size={28} aria-hidden="true" />
        <p className="section-eyebrow">Taslim.ai</p>
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
