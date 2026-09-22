import { DocumentStudioView } from "@/components/DocumentStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function DocumentStudioPage() {
  return <ProtectedPage><DocumentStudioView /></ProtectedPage>;
}
