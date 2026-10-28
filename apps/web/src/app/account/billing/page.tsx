import { BillingView } from "@/components/BillingView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function BillingPage() {
  await requireAuthenticatedPage("/account/billing");
  return <ProtectedPage><BillingView /></ProtectedPage>;
}
