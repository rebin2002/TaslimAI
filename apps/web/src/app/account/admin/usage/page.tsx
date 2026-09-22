import { AdminUsageView } from "@/components/AdminUsageView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function AdminUsagePage() {
  return <ProtectedPage><AdminUsageView /></ProtectedPage>;
}
