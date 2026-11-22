import { notFound } from "next/navigation";
import { departments } from "@/lib/data";
import { ProtectedPage } from "@/components/ProtectedPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";
import { PlaceholderPage } from "@/app/placeholder-page";

export const dynamic = "force-dynamic";

export default async function DepartmentPlaceholderPage({ params }: { params: Promise<{ department: string }> }) {
  const { department } = await params;
  const entry = departments.find((item) => item.id === department);
  if (!entry) notFound();
  await requireAuthenticatedPage(`/${department}`);
  return <ProtectedPage><PlaceholderPage nameKey={entry.titleKey} /></ProtectedPage>;
}
