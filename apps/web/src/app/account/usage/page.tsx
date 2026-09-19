import { ProtectedPage } from "@/components/ProtectedPage";
import { UsageView } from "@/components/UsageView";

export default function UsagePage() {
  return <ProtectedPage><UsageView /></ProtectedPage>;
}
