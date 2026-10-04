"use client";

/* The root fallback must hard-navigate because the Next router may be part of the failed tree. */
/* eslint-disable @next/next/no-html-link-for-pages */

/**
 * The root layout is outside app/error.tsx. Keep this fallback dependency-free
 * so it can render when LocaleProvider, AuthProvider, or AppShell failed.
 * Never expose the error object: it may contain private or provider details.
 */
export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  if (typeof window !== "undefined") {
    console.error("Taslim global route recovery", error.digest ? "digest-present" : "digest-unavailable");
  }

  return (
    <html lang="en" dir="ltr">
      <body style={{ margin: 0, background: "#f7f9fc", color: "#172639", fontFamily: "system-ui, sans-serif" }}>
        <main style={{ minHeight: "100vh", display: "grid", placeItems: "center", padding: "24px" }}>
          <section role="alert" aria-live="assertive" style={{ maxWidth: "530px", padding: "48px 32px", textAlign: "center", border: "1px solid #dbe4ec", borderRadius: "18px", background: "white", boxShadow: "0 20px 50px rgba(23,38,57,.07)" }}>
            <p style={{ margin: 0, color: "#6b7d8e", fontSize: "11px", fontWeight: 800, letterSpacing: ".12em", textTransform: "uppercase" }}>Taslim.ai</p>
            <h1 style={{ margin: "12px 0 14px", fontSize: "32px", letterSpacing: "-.05em" }}>Something went wrong</h1>
            <p style={{ margin: 0, color: "#647587", fontSize: "14px", lineHeight: 1.7 }}>We could not load this workspace safely. Try again or return to your assets.</p>
            <div style={{ display: "flex", justifyContent: "center", gap: "9px", marginTop: "28px", flexWrap: "wrap" }}>
              <button type="button" onClick={reset} style={{ cursor: "pointer", border: 0, borderRadius: "8px", padding: "11px 14px", color: "white", background: "#172639", fontSize: "11px", fontWeight: 800 }}>Try again</button>
              <a href="/assets" style={{ display: "inline-flex", alignItems: "center", borderRadius: "8px", padding: "11px 14px", color: "#337cf6", background: "#eef5ff", fontSize: "11px", fontWeight: 800, textDecoration: "none" }}>Open Assets</a>
            </div>
          </section>
        </main>
      </body>
    </html>
  );
}
