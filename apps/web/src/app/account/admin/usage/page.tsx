import { AdminUsageView } from "@/components/AdminUsageView";
import { AdminPage } from "@/components/AdminPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function AdminUsagePage() {
  await requireAuthenticatedPage("/account/admin/usage");
  return <AdminPage><AdminUsageView /></AdminPage>;
}
