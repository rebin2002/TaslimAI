import { ProtectedPage } from "@/components/ProtectedPage";
import { SocialStudioView } from "@/components/SocialStudioView";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function SocialPage() {
  await requireAuthenticatedPage("/create/social");
  return <ProtectedPage><SocialStudioView /></ProtectedPage>;
}
