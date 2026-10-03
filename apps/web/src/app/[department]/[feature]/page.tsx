import { notFound } from "next/navigation";
import { departments } from "@/lib/data";
import { HealthPlaceholderPage } from "@/components/HealthPlaceholderPage";
import { ProtectedPage } from "@/components/ProtectedPage";
import { PlaceholderPage } from "@/app/placeholder-page";

export default async function FeaturePlaceholderPage({ params }: { params: Promise<{ department: string; feature: string }> }) {
  const { department, feature } = await params;
  const entry = departments.find((item) => item.id === department)?.features.find((item) => item.id === feature);
  if (!entry) notFound();
  const page = department === "personal" && feature === "health"
    ? <HealthPlaceholderPage />
    : <PlaceholderPage nameKey={entry.labelKey} />;
  return <ProtectedPage>{page}</ProtectedPage>;
}
