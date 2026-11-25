import { ProtectedPage } from "@/components/ProtectedPage";
import { VoiceStudioView } from "@/components/VoiceStudioView";

import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function VoiceStudioPage() {
  await requireAuthenticatedPage("/create/voice");
  return <ProtectedPage><VoiceStudioView /></ProtectedPage>;
}
