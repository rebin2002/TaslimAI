import { PlaceholderPage } from "@/app/placeholder-page";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function DevelopmentProjectsPage() {
  await requireAuthenticatedPage("/development/projects");
  return <ProtectedPage><PlaceholderPage nameKey="feature.projects" /></ProtectedPage>;
}
