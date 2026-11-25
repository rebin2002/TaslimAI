import { requireAuthenticatedPage } from "@/lib/serverAuth";
import { StudioChooser } from "@/components/StudioChooser";

export const dynamic = "force-dynamic";

export default async function CreatePage() {
  await requireAuthenticatedPage("/create");
  return <div className="create-page"><StudioChooser /></div>;
}
