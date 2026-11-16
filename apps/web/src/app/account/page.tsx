import { AccountView } from "@/components/AccountView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function AccountPage() {
  await requireAuthenticatedPage("/account");
  return <ProtectedPage><AccountView /></ProtectedPage>;
}
