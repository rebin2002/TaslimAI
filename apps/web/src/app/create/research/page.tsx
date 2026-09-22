import { ProtectedPage } from "@/components/ProtectedPage";
import { ResearchStudioView } from "@/components/ResearchStudioView";

export default function ResearchStudioPage() {
  return <ProtectedPage><ResearchStudioView /></ProtectedPage>;
}
