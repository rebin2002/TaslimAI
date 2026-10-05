import { DevelopmentBuildChecklist } from "@/components/DevelopmentBuildChecklist";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function DevelopmentBuildPage() {
  await requireAuthenticatedPage("/development/build");
  return <ProtectedPage><DevelopmentBuildChecklist /></ProtectedPage>;
}
