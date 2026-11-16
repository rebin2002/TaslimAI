"use client";

import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useEffect, useRef, type RefObject } from "react";
import { useLocale } from "@/components/LocaleProvider";
import { restoreRecoveryFocus } from "@/lib/recoveryFocus";

export function MusicStudioRecoveryView({
  reset,
  t,
  panelRef,
}: Readonly<{
  reset: () => void;
  t: (key: string) => string;
  panelRef?: RefObject<HTMLElement | null>;
}>) {
  return (
    <main className="placeholder-page">
      <section
        ref={panelRef}
        className="placeholder-panel"
        tabIndex={-1}
        role="alert"
        aria-live="assertive"
        aria-labelledby="music-error-title"
        aria-describedby="music-error-description"
      >
        <RefreshCw className="placeholder-symbol" size={28} aria-hidden="true" />
        <p className="section-eyebrow">{t("music.title")}</p>
        <h1 id="music-error-title">{t("error.title")}</h1>
        <p id="music-error-description">{t("error.description")}</p>
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

export default function MusicStudioError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const { t } = useLocale();
  const panelRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    console.error("Music Studio route recovery", "route_error");
  }, []);

  // Recovery focus restoration. React unmounts the crashed subtree together
  // with the element that held focus, so focus silently drops to
  // document.body; keyboard and screen-reader users would then have to
  // re-traverse the app shell and the mobile bottom navigation to reach
  // "Try again", and the assertive recovery message is easy to miss. The
  // shared decision moves focus onto the panel only when it is not already
  // inside it. The effect is keyed on the error identity rather than the
  // digest because a retry that fails again can report the same digest,
  // which would skip the restore on exactly the attempt the user retried.
  useEffect(() => {
    restoreRecoveryFocus(panelRef.current, typeof document === "undefined" ? null : document.activeElement);
  }, [error]);

  return <MusicStudioRecoveryView reset={reset} t={t} panelRef={panelRef} />;
}
