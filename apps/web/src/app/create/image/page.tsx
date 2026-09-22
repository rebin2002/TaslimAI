import { ImageStudioView } from "@/components/ImageStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function ImageStudioPage() {
  return <ProtectedPage><ImageStudioView /></ProtectedPage>;
}
