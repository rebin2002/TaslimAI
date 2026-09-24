import { ActivityCenterView } from "@/components/ActivityCenterView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function ActivityPage() {
  return <ProtectedPage><ActivityCenterView /></ProtectedPage>;
}
