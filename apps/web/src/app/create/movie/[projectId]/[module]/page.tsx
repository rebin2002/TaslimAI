import { FullMovieWorkspaceView } from "@/components/FullMovieWorkspaceView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

type MovieModulePageProps = {
  params: Promise<{ projectId: string; module: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
};

function returnPathSegment(segment: string): string {
  // Next may supply an already escaped segment. Preserve each valid escape while
  // encoding plain delimiters and any literal percent that is not an escape.
  return encodeURIComponent(segment).replace(/%25([0-9a-f]{2})/gi, "%$1");
}

export default async function FullMovieModulePage({ params, searchParams }: MovieModulePageProps) {
  const [{ projectId, module }, query] = await Promise.all([params, searchParams]);
  const pathname = `/create/movie/${returnPathSegment(projectId)}/${returnPathSegment(module)}`;
  await requireAuthenticatedPage(internalReturnPath(pathname, query));
  return <ProtectedPage><FullMovieWorkspaceView projectId={projectId} module={module} /></ProtectedPage>;
}
