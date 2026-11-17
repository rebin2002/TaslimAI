import type { Metadata } from "next";
import { ProtectedPage } from "@/components/ProtectedPage";
import { ProductAdInputWizardPage } from "@/components/ProductAdInputWizardPage";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";
export const metadata: Metadata = {
  robots: {
    index: false,
    follow: false,
    noarchive: true,
    nosnippet: true,
  },
};

export default async function ProductAdPreviewRoute() {
  await requireAuthenticatedPage("/create/product-ad");
  return <ProtectedPage><ProductAdInputWizardPage /></ProtectedPage>;
}
