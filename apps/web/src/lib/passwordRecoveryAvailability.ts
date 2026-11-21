import { API_URL } from "./apiBase";

export type PasswordRecoveryAvailability = { available: boolean };

export async function getPasswordRecoveryAvailability(): Promise<PasswordRecoveryAvailability> {
  const response = await fetch(`${API_URL}/api/auth/password-recovery/availability`, {
    credentials: "include",
    cache: "no-store",
  });
  if (!response.ok) throw new Error("Password recovery availability is unavailable.");

  const body = await response.json().catch(() => null) as { available?: unknown } | null;
  if (!body || typeof body.available !== "boolean")
    throw new Error("Password recovery availability response is invalid.");

  return { available: body.available };
}
