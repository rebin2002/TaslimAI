"use client";

import { useEffect } from "react";
import Link from "next/link";
import { RefreshCw } from "lucide-react";

export default function PresentationStudioError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  useEffect(() => { console.error("Presentation Studio route recovery", "route_error"); }, []);
  return <main className="placeholder-page"><section className="placeholder-panel"><RefreshCw className="placeholder-symbol" size={28} /><p className="section-eyebrow">Taslim Presentation Studio</p><h1>Something went wrong</h1><p>We could not load this workspace safely. Try again or return to your assets.</p><div className="placeholder-actions"><button className="primary-button" onClick={reset}>Try again</button><Link className="secondary-button" href="/assets">Open assets</Link></div></section></main>;
}
