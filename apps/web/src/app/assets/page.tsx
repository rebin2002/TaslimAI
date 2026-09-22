import { AssetsView } from "@/components/AssetsView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default async function AssetsPage({ searchParams }: { searchParams: Promise<{ projectId?: string }> }) {
  const filters = await searchParams;
  return <ProtectedPage><AssetsView initialProjectId={filters.projectId} /></ProtectedPage>;
}
