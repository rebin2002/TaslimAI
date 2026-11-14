import { MovieStudioView } from "@/components/MovieStudioView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function MoviePage() {
  await requireAuthenticatedPage("/create/movie");
  return <ProtectedPage><MovieStudioView /></ProtectedPage>;
}
