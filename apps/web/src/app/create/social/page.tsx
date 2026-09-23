import { ProtectedPage } from "@/components/ProtectedPage";
import { SocialStudioView } from "@/components/SocialStudioView";

export default function SocialPage() {
  return <ProtectedPage><SocialStudioView /></ProtectedPage>;
}
