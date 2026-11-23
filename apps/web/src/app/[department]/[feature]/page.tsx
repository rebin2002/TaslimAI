import { notFound } from "next/navigation";
import { departments } from "@/lib/data";
import { ProtectedPage } from "@/components/ProtectedPage";
import { PlaceholderPage } from "@/app/placeholder-page";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function FeaturePlaceholderPage({ params }: { params: Promise<{ department: string; feature: string }> }) {
  const { department, feature } = await params;
  await requireAuthenticatedPage(`/${department}/${feature}`);
  const entry = departments.find((item) => item.id === department)?.features.find((item) => item.id === feature);
  if (!entry) notFound();
  return <ProtectedPage><PlaceholderPage nameKey={entry.labelKey} /></ProtectedPage>;
}
