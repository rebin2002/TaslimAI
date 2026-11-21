import { ImageStudioView } from "@/components/ImageStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function ImageStudioPage() {
  await requireAuthenticatedPage("/create/image");
  return <ProtectedPage><ImageStudioView /></ProtectedPage>;
}
