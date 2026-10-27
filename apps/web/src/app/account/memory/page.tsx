import { MemoryView } from "@/components/MemoryView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function MemoryPage() {
  await requireAuthenticatedPage("/account/memory");
  return <ProtectedPage><MemoryView /></ProtectedPage>;
}
