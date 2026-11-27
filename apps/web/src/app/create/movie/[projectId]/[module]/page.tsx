import { FullMovieWorkspaceView } from "@/components/FullMovieWorkspaceView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

type MovieModulePageProps = {
  params: Promise<{ projectId: string; module: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
};

export default async function FullMovieModulePage({ params, searchParams }: MovieModulePageProps) {
  const [{ projectId, module }, query] = await Promise.all([params, searchParams]);
  const pathname = `/create/movie/${encodeURIComponent(projectId)}/${encodeURIComponent(module)}`;
  await requireAuthenticatedPage(internalReturnPath(pathname, query));
  return <ProtectedPage><FullMovieWorkspaceView projectId={projectId} module={module} /></ProtectedPage>;
}
