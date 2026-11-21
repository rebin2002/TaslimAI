import { PresentationStudioView } from "@/components/PresentationStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function PresentationStudioPage() {
  await requireAuthenticatedPage("/create/presentation");
  return <ProtectedPage><PresentationStudioView /></ProtectedPage>;
}
