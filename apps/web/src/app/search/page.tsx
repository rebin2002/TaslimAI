import { GlobalSearchView } from "@/components/GlobalSearchView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function SearchPage() {
  await requireAuthenticatedPage("/search");
  return <ProtectedPage><GlobalSearchView /></ProtectedPage>;
}
