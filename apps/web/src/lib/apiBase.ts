export const API_URL = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");

export const assetFileUrl = (assetId: string, inline = false) =>
  `${API_URL}/api/assets/${assetId}/download${inline ? "?inline=true" : ""}`;
