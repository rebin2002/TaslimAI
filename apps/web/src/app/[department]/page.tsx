import { notFound } from "next/navigation";
import { departments } from "@/lib/data";
import { PlaceholderPage } from "@/app/placeholder-page";

export default async function DepartmentPlaceholderPage({ params }: { params: Promise<{ department: string }> }) {
  const { department } = await params;
  const entry = departments.find((item) => item.id === department);
  if (!entry) notFound();
  return <PlaceholderPage nameKey={entry.titleKey} />;
}
