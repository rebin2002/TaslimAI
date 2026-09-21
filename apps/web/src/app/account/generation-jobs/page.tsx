import { GenerationJobsView } from "@/components/GenerationJobsView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function GenerationJobsPage() {
  return <ProtectedPage><GenerationJobsView /></ProtectedPage>;
}
