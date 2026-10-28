import { ProtectedPage } from "@/components/ProtectedPage";
import { UsageView } from "@/components/UsageView";

import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function UsagePage() {
  await requireAuthenticatedPage("/account/usage");
  return <ProtectedPage><UsageView /></ProtectedPage>;
}
