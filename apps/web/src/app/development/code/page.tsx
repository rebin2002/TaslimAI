import { PlaceholderPage } from "@/app/placeholder-page";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function DevelopmentCodePage() {
  await requireAuthenticatedPage("/development/code");
  return <ProtectedPage><PlaceholderPage nameKey="feature.code" /></ProtectedPage>;
}
