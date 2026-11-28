"use client";

import { useEffect, useRef } from "react";
import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";

/**
 * Keep unexpected Health render failures generic. Error objects can contain
 * provider, request, or user-content details and must never be rendered here.
 */
export default function HealthRouteError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const { t } = useLocale();
  const panelRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    console.error("Taslim health route recovery", error.digest ? "digest-present" : "digest-unavailable");
  }, [error.digest]);

  // Recovery focus restoration. When this boundary mounts, React removes the
  // crashed subtree along with any element that held focus, so focus drops to
  // document.body. Keyboard and screen-reader users would otherwise have to
  // re-traverse the whole app shell to reach the retry control, and the
  // assertive recovery message is easy to miss. Moving focus onto the panel
  // announces it and puts the recovery actions next in tab order. The effect is
  // keyed on the error identity rather than the digest because a retry that
  // fails again can report the same digest, which would skip the restore.
  useEffect(() => {
    const panel = panelRef.current;
    if (!panel) return;
    if (panel.contains(document.activeElement)) return;
    panel.focus();
  }, [error]);

  return (
    <div className="placeholder-page">
      <section ref={panelRef} className="placeholder-panel" tabIndex={-1} role="alert" aria-live="assertive" aria-labelledby="health-error-title" aria-describedby="health-error-description">
        <RefreshCw className="placeholder-symbol" size={28} aria-hidden="true" />
        <p className="section-eyebrow">{t("health.eyebrow")}</p>
        <h1 id="health-error-title">{t("error.title")}</h1>
        <p id="health-error-description">{t("error.description")}</p>
        <div className="placeholder-actions">
          <button className="primary-button" type="button" onClick={reset}>
            <RefreshCw size={15} aria-hidden="true" /> {t("error.retry")}
          </button>
          <Link className="secondary-button" href="/">{t("placeholder.backHome")}</Link>
        </div>
      </section>
    </div>
  );
}
