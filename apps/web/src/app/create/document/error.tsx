"use client";

import Link from "next/link";
import { RefreshCw } from "lucide-react";
import { useEffect, useRef } from "react";
import { useLocale } from "@/components/LocaleProvider";
import { restoreRecoveryFocus } from "@/lib/recoveryFocus";

/**
 * Route recovery boundary for Document Studio. It takes over when a document
 * workspace or an export/download step throws, offering the retry and
 * "open assets" escape. Only the error identity is observed: messages can carry
 * document or provider details and must never be rendered or logged.
 */
export default function DocumentStudioError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const { t } = useLocale();
  const panelRef = useRef<HTMLElement | null>(null);

  // Recovery focus restoration. React unmounts the crashed document workspace
  // together with the element that held focus - usually the export button just
  // activated - so focus silently drops to document.body. Keyboard and
  // screen-reader users would then have to re-traverse the app shell and the
  // mobile bottom navigation to reach "Try again", and the assertive recovery
  // message is easy to miss. The shared decision used by the generic app and
  // Music Studio boundaries moves focus onto the panel only when it is not
  // already inside it. It is keyed on the error identity rather than the digest
  // because a retry that fails again can report the same digest, which would
  // skip the restore on exactly the attempt the user retried.
  useEffect(() => {
    restoreRecoveryFocus(panelRef.current, typeof document === "undefined" ? null : document.activeElement);
  }, [error]);

  return <div className="protected-page"><section ref={panelRef} className="account-card document-error-state" tabIndex={-1} role="alert" aria-live="assertive" aria-labelledby="document-error-title" aria-describedby="document-error-description"><p className="section-eyebrow">{t("document.resultEyebrow")}</p><h1 id="document-error-title">{t("document.completedLoadError")}</h1><p id="document-error-description">{t("document.completedLoadHint")}</p><div className="document-result-actions"><button className="primary-button" type="button" onClick={reset}><RefreshCw size={15} aria-hidden="true" /> {t("document.retry")}</button><Link className="secondary-button" href="/assets">{t("document.openAssets")}</Link></div></section></div>;
}
