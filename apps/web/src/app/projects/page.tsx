import { ProjectsView } from "@/components/ProjectsView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function ProjectsPage() {
  await requireAuthenticatedPage("/projects");
  return <ProtectedPage><ProjectsView /></ProtectedPage>;
}
