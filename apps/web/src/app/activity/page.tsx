import { ActivityCenterView } from "@/components/ActivityCenterView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function ActivityPage() {
  await requireAuthenticatedPage("/activity");
  return <ProtectedPage><ActivityCenterView /></ProtectedPage>;
}
