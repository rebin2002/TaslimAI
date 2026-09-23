import { ActivityCenterView } from "@/components/ActivityCenterView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function NotificationsPage() {
  return <ProtectedPage><ActivityCenterView /></ProtectedPage>;
}
