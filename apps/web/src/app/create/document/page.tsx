import { DocumentStudioView } from "@/components/DocumentStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function DocumentStudioPage() {
  await requireAuthenticatedPage("/create/document");
  return <ProtectedPage><DocumentStudioView /></ProtectedPage>;
}
