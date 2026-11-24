import { MemoryView } from "@/components/MemoryView";
import { ProtectedPage } from "@/components/ProtectedPage";
import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function MemoryPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const query = await searchParams;
  // MemoryView reads no query state today, but the client boundary this gate
  // fronts already returns the full location on an expired session; keep the
  // promised destination identical rather than narrowing it to the pathname.
  await requireAuthenticatedPage(internalReturnPath("/account/memory", query));
  return <ProtectedPage><MemoryView /></ProtectedPage>;
}
