"use client";
/**
 * Issue #1259 (Academic Research Studio) — route recovery boundary.
 *
 * Takes over when the academic surface throws, offering retry plus the escape
 * back to the studio and to Assets. Only the error identity is observed: an
 * exception message can carry brief or source text, so nothing but an opaque
 * marker and the digest is logged, and the message itself is never rendered.
 *
 * The shared layout already provides the `main` landmark, so this panel does not
 * add a second one, and focus is moved into the panel only when it is not
 * already inside it.
 */
import Link from "next/link";
import { useEffect, useMemo, useRef } from "react";
import { RefreshCw } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { restoreRecoveryFocus } from "@/lib/recoveryFocus";
import { academicAssetsRoute, academicRoute } from "@/components/academic/academicRoutes";
import { createAcademicTranslator } from "@/components/academic/academicStrings";

export default function AcademicResearchStudioError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const { t, locale } = useLocale();
  const ta = useMemo(() => createAcademicTranslator(locale), [locale]);
  const panelRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    // Keyed on the error identity, not the digest: a retry that fails again can
    // report the same digest, and that is exactly the attempt that needs focus.
    restoreRecoveryFocus(panelRef.current, typeof document === "undefined" ? null : document.activeElement);
    console.error("Academic Research Studio route recovery", "route_error");
  }, [error]);

  return (
    <div className="placeholder-page">
      <section
        ref={panelRef}
        className="placeholder-panel"
        tabIndex={-1}
        role="alert"
        aria-live="assertive"
        aria-labelledby="academic-error-title"
        aria-describedby="academic-error-description"
      >
        <RefreshCw className="placeholder-symbol" size={28} />
        <p className="section-eyebrow">{t("research.title")}</p>
        <h1 id="academic-error-title">{t("error.title")}</h1>
        <p id="academic-error-description">{t("error.description")}</p>
        <div className="placeholder-actions">
          <button className="primary-button" type="button" onClick={reset}>
            <RefreshCw size={15} aria-hidden="true" /> {t("error.retry")}
          </button>
          <Link className="secondary-button" href={academicRoute}>
            {ta("routeError.back")}
          </Link>
          <Link className="secondary-button" href={academicAssetsRoute}>
            {t("error.openAssets")}
          </Link>
        </div>
      </section>
    </div>
  );
}
