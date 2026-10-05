import { DevelopmentHub } from "@/components/DevelopmentHub";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function DevelopmentPage() {
  await requireAuthenticatedPage("/development");
  return <ProtectedPage><DevelopmentHub /></ProtectedPage>;
}
