import { MusicStudioView } from "@/components/MusicStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function MusicStudioPage() {
  return <ProtectedPage><MusicStudioView /></ProtectedPage>;
}
