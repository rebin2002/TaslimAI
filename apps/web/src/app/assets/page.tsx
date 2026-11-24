import { AssetsView } from "@/components/AssetsView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function AssetsPage({ searchParams }: { searchParams: Promise<{ assetId?: string; projectId?: string; search?: string; status?: "Active" | "Archived" }> }) {
  const filters = await searchParams;
  // The library hydrates its filters from assetId/projectId/search/status, so
  // the server gate must promise back the whole deep link, not only `/assets`.
  await requireAuthenticatedPage(internalReturnPath("/assets", filters));
  return <ProtectedPage><AssetsView initialAssetId={filters.assetId} initialProjectId={filters.projectId} initialSearch={filters.search} initialStatus={filters.status} /></ProtectedPage>;
}
