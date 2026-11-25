"use client";

import { useLocale } from "@/components/LocaleProvider";
import { useAuth } from "@/components/AuthProvider";
import { ProductAdInputWizard } from "@/components/ProductAdInputWizard";

export function ProductAdInputWizardPage() {
  const { locale } = useLocale();
  const { user, workspace, loading } = useAuth();
  if (loading || !user || !workspace) return null;
  return <ProductAdInputWizard key={`${user.id}:${workspace.id}`} locale={locale} principal={{ userId: user.id, workspaceId: workspace.id }} />;
}
