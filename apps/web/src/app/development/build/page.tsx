import { DevelopmentBuildChecklist } from "@/components/DevelopmentBuildChecklist";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function DevelopmentBuildPage() {
  return <ProtectedPage><DevelopmentBuildChecklist /></ProtectedPage>;
}
