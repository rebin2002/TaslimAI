import { DevelopmentHub } from "@/components/DevelopmentHub";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function DevelopmentPage() {
  return <ProtectedPage><DevelopmentHub /></ProtectedPage>;
}
