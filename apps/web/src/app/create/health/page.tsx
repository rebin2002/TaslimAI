import type { Metadata } from "next";
import { HealthCareConsentJourney } from "@/components/HealthCareConsentJourney";
import { HealthPlaceholderPage } from "@/components/HealthPlaceholderPage";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";
export const metadata: Metadata = {
  robots: {
    index: false,
    follow: false,
    noarchive: true,
    nosnippet: true,
  },
};

export default async function HealthCarePage() {
  await requireAuthenticatedPage("/create/health");
  return <ProtectedPage>{/* #714 wiring: #804 opt-in consent journey over synthetic samples only. */}<HealthPlaceholderPage /><HealthCareConsentJourney /></ProtectedPage>;
}
