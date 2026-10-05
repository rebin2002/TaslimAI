import { HealthPlaceholderPage } from "@/components/HealthPlaceholderPage";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function HealthPage() {
  await requireAuthenticatedPage("/personal/health");
  return <ProtectedPage><HealthPlaceholderPage /></ProtectedPage>;
}
