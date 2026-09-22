import { PresentationStudioView } from "@/components/PresentationStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function PresentationStudioPage() {
  return <ProtectedPage><PresentationStudioView /></ProtectedPage>;
}
