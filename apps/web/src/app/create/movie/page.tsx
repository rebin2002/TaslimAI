import { MovieStudioView } from "@/components/MovieStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function MoviePage() {
  return <ProtectedPage><MovieStudioView /></ProtectedPage>;
}
