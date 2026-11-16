"use client";

import { useAuth } from "@/components/AuthProvider";
import { HealthCareConsentJourney } from "@/components/HealthCareConsentJourney";

/**
 * Consent is strictly scoped to the currently authenticated principal.
 * React remounts the in-memory consent store on a user/workspace change,
 * including a workspace switch that does not navigate away from this route.
 * Never show the previous principal's sample selection while auth refreshes.
 */
export function healthCareConsentScopeKey(
  userId: string | null | undefined,
  workspaceId: string | null | undefined,
): string | null {
  if (!userId || !workspaceId) return null;
  return JSON.stringify([userId, workspaceId]);
}

export function HealthCareScopedConsentJourney() {
  const { user, workspace, loading } = useAuth();
  const scope = loading ? null : healthCareConsentScopeKey(user?.id, workspace?.id);
  if (scope === null) return null;
  return <HealthCareConsentJourney key={scope} />;
}
