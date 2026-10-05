import { DevelopmentDebugGuide } from "@/components/DevelopmentDebugGuide";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function DevelopmentDebugPage() {
  await requireAuthenticatedPage("/development/debug");
  return <ProtectedPage><DevelopmentDebugGuide /></ProtectedPage>;
}
