import { GenerationJobsView } from "@/components/GenerationJobsView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function GenerationJobsPage() {
  await requireAuthenticatedPage("/account/generation-jobs");
  return <ProtectedPage><GenerationJobsView /></ProtectedPage>;
}
