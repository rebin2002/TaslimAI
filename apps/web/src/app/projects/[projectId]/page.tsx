import { ProjectDetailView } from "@/components/ProjectDetailView";
import { ProtectedPage } from "@/components/ProtectedPage";
export default function ProjectPage() { return <ProtectedPage><ProjectDetailView /></ProtectedPage>; }
