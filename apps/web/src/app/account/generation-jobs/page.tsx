import { GenerationJobsView } from "@/components/GenerationJobsView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function GenerationJobsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const query = await searchParams;
  // GenerationJobsView reads no query state today, but the client boundary this
  // gate fronts already returns the full location on an expired session; keep
  // the promised destination identical rather than narrowing it to pathname.
  await requireAuthenticatedPage(internalReturnPath("/account/generation-jobs", query));
  return <ProtectedPage><GenerationJobsView /></ProtectedPage>;
}
