"use client";
import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";

/**
 * Session-revalidation boundary for the Memory view.
 *
 * This is the body of the route's previous `layout.tsx`, moved verbatim so the
 * layout slot can become empty. A `"use client"` layout renders the page as a
 * `children` prop, and a `redirect()` thrown by the page underneath that
 * boundary is reported to the nearest error boundary instead of becoming a
 * navigation: the gate returned an HTTP 200 error page rather than the 307 to
 * sign-in that `requireAuthenticatedPage` promises. `src/app/error.tsx` was the
 * only thing standing between an anonymous visitor and the Memory tree, which
 * is the exact defense-in-depth layer the server gate exists to provide.
 *
 * Rendering it from inside the page, below the gate, keeps both behaviours:
 * the server still answers an unverified session with a single 307 hop, and the
 * client still re-validates when the tab regains visibility or focus and remounts
 * the view when the session or workspace identity changes.
 */
export default function MemorySessionBoundary({ children }: Readonly<{ children: React.ReactNode }>) {
  const { user, workspace, loading, refresh } = useAuth();
  const [checkingSession, setCheckingSession] = useState(false);
  const refreshInFlight = useRef(false);

  useEffect(() => {
    let mounted = true;
    const revalidateVisibleSession = () => {
      if (document.visibilityState !== "visible" || refreshInFlight.current) return;
      refreshInFlight.current = true;
      setCheckingSession(true);
      void refresh().finally(() => {
        refreshInFlight.current = false;
        if (mounted) setCheckingSession(false);
      });
    };
    document.addEventListener("visibilitychange", revalidateVisibleSession);
    window.addEventListener("focus", revalidateVisibleSession);
    return () => {
      mounted = false;
      document.removeEventListener("visibilitychange", revalidateVisibleSession);
      window.removeEventListener("focus", revalidateVisibleSession);
    };
  }, [refresh]);

  if (loading || checkingSession) {
    return <div className="loading-state" role="status" aria-busy="true"><span className="loading-spinner" /></div>;
  }
  const sessionKey = `${user?.id ?? "signed-out"}:${workspace?.id ?? "no-workspace"}`;
  return <div key={sessionKey}>{children}</div>;
}
