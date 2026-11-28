import { PresentationStudioView } from "@/components/PresentationStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function PresentationStudioPage({ searchParams }: { searchParams: Promise<{ projectId?: string | string[] }> }) {
  const query = await searchParams;
  // PresentationStudioView selects its project from ?projectId, so an
  // anonymous or expired visit must return to the same studio selection.
  await requireAuthenticatedPage(internalReturnPath("/create/presentation", query));
  return <ProtectedPage><PresentationStudioView /></ProtectedPage>;
}
