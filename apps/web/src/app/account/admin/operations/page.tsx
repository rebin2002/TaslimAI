import { AdminOperationsView } from "@/components/AdminOperationsView";
import { AdminPage } from "@/components/AdminPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function AdminOperationsPage() {
  await requireAuthenticatedPage("/account/admin/operations");
  return <AdminPage><AdminOperationsView /></AdminPage>;
}
