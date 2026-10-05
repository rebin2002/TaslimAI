import { DevelopmentLearningGuide } from "@/components/DevelopmentLearningGuide";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function DevelopmentLearnPage() {
  await requireAuthenticatedPage("/development/learn");
  return <ProtectedPage><DevelopmentLearningGuide /></ProtectedPage>;
}
