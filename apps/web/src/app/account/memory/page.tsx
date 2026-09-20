import { MemoryView } from "@/components/MemoryView";
import { ProtectedPage } from "@/components/ProtectedPage";

export default function MemoryPage() {
  return <ProtectedPage><MemoryView /></ProtectedPage>;
}
