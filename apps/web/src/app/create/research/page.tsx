import { ProtectedPage } from "@/components/ProtectedPage";
import { ResearchStudioView } from "@/components/ResearchStudioView";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function ResearchStudioPage() {
  await requireAuthenticatedPage("/create/research");
  return <ProtectedPage><ResearchStudioView /></ProtectedPage>;
}
