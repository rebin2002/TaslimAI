import { FullMovieWorkspaceView } from "@/components/FullMovieWorkspaceView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function FullMovieModulePage({ params }: { params: Promise<{ projectId: string; module: string }> }) {
  const { projectId, module } = await params;
  await requireAuthenticatedPage(`/create/movie/${projectId}/${module}`);
  return <ProtectedPage><FullMovieWorkspaceView projectId={projectId} module={module} /></ProtectedPage>;
}
