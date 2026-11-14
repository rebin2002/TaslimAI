"use client";

import { useEffect, useRef } from "react";
import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { restoreRecoveryFocus } from "@/lib/recoveryFocus";

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
  const panelRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    console.error("Taslim route recovery", error.digest ? "digest-present" : "digest-unavailable");
  }, [error.digest]);

  // Recovery focus restoration. This boundary mounts inside AppShell's
  // <main id="main-content">, and React removes the crashed subtree along with
  // whatever element held focus - on a phone usually the control that was just
  // tapped - so focus silently drops to document.body. Keyboard and
  // screen-reader users would then have to re-traverse the app shell and the
  // fixed mobile bottom navigation to reach the retry action, and the assertive
  // recovery message is easy to miss. Moving focus onto the panel announces it
  // and puts the recovery actions next in tab order. The effect is keyed on the
  // error identity rather than the digest because a retry that fails again can
  // report the same digest, which would skip the restore on exactly the attempt
  // where the user pressed "Try again".
  useEffect(() => {
    restoreRecoveryFocus(panelRef.current, typeof document === "undefined" ? null : document.activeElement);
  }, [error]);

  // A plain wrapper keeps one main landmark, so mobile landmark navigation does
  // not offer two indistinguishable "main" targets.
  return (
    <div className="placeholder-page">
      <section
        ref={panelRef}
        className="placeholder-panel"
        tabIndex={-1}
        role="alert"
        aria-live="assertive"
        aria-labelledby="app-error-title"
        aria-describedby="app-error-description"
      >
        <RefreshCw className="placeholder-symbol" size={28} aria-hidden="true" />
        <p className="section-eyebrow">Taslim.ai</p>
        <h1 id="app-error-title">{t("error.title")}</h1>
        <p id="app-error-description">{t("error.description")}</p>
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
