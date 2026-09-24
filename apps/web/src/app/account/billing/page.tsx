import { BillingView } from "@/components/BillingView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function BillingPage() {
  return <ProtectedPage><BillingView /></ProtectedPage>;
}
