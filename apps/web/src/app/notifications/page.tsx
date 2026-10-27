import { NotificationCenterView } from "@/components/NotificationCenterView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function NotificationsPage() {
  await requireAuthenticatedPage("/notifications");
  return <ProtectedPage><NotificationCenterView /></ProtectedPage>;
}
