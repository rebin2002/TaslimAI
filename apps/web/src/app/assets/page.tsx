import { AssetsView } from "@/components/AssetsView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default async function AssetsPage({ searchParams }: { searchParams: Promise<{ assetId?: string; projectId?: string; search?: string; status?: "Active" | "Archived" }> }) {
  const filters = await searchParams;
  return <ProtectedPage><AssetsView initialAssetId={filters.assetId} initialProjectId={filters.projectId} initialSearch={filters.search} initialStatus={filters.status} /></ProtectedPage>;
}
