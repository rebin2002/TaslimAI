import { GlobalSearchView } from "@/components/GlobalSearchView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function SearchPage() {
  return <ProtectedPage><GlobalSearchView /></ProtectedPage>;
}
