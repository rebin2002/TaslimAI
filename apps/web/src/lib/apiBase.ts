const configuredApiUrl = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");

// Production browser requests use the same-origin Next.js proxy so mobile
// browsers do not need to accept an API-host cookie from a separate origin.
// Local development keeps the direct API fallback for the existing workflow.
export const API_URL = process.env.NODE_ENV === "production" ? "" : configuredApiUrl;

export const assetFileUrl = (assetId: string, inline = false) =>
  `${API_URL}/api/assets/${assetId}/download${inline ? "?inline=true" : ""}`;
