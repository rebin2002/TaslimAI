import { ProjectDetailView } from "@/components/ProjectDetailView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function ProjectPage() {
  await requireAuthenticatedPage("/projects");
  return <ProtectedPage><ProjectDetailView /></ProtectedPage>;
}
