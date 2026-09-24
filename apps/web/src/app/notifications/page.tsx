import { NotificationCenterView } from "@/components/NotificationCenterView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function NotificationsPage() {
  return <ProtectedPage><NotificationCenterView /></ProtectedPage>;
}
