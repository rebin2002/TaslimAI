import { ProtectedPage } from "@/components/ProtectedPage";
import { VoiceStudioView } from "@/components/VoiceStudioView";

export default function VoiceStudioPage() {
  return <ProtectedPage><VoiceStudioView /></ProtectedPage>;
}
