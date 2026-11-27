import { ProtectedPage } from "@/components/ProtectedPage";
import { AcademicTemplateGalleryContainer } from "@/components/academic/templates/AcademicTemplateGalleryContainer";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

export default async function AcademicTemplatesPage() {
  await requireAuthenticatedPage("/create/academic-templates");
  return (
    <ProtectedPage>
      <AcademicTemplateGalleryContainer />
    </ProtectedPage>
  );
}
