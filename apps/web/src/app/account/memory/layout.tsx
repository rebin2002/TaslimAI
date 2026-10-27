"use client";

import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/AuthProvider";

export default function MemoryLayout({ children }: Readonly<{ children: React.ReactNode }>) {
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
