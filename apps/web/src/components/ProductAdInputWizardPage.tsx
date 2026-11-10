"use client";

import { useLocale } from "@/components/LocaleProvider";
import { ProductAdInputWizard } from "@/components/ProductAdInputWizard";

export function ProductAdInputWizardPage() {
  const { locale } = useLocale();
  return <ProductAdInputWizard locale={locale} />;
}
