import { MusicStudioView } from "@/components/MusicStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";

import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function MusicStudioPage() {
  await requireAuthenticatedPage("/create/music");
  return <ProtectedPage><MusicStudioView /></ProtectedPage>;
}
