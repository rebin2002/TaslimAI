import { DevelopmentLearningGuide } from "@/components/DevelopmentLearningGuide";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function DevelopmentLearnPage() {
  return <ProtectedPage><DevelopmentLearningGuide /></ProtectedPage>;
}
