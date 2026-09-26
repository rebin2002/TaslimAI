import { FullMovieWorkspaceView } from "@/components/FullMovieWorkspaceView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default async function FullMovieModulePage({ params }: { params: Promise<{ projectId: string; module: string }> }) {
  const { projectId, module } = await params;
  return <ProtectedPage><FullMovieWorkspaceView projectId={projectId} module={module} /></ProtectedPage>;
}
