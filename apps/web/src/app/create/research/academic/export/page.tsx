import type { Metadata } from "next";
import { ProtectedPage } from "@/components/ProtectedPage";
import { AcademicReportExportPanel } from "@/components/academic/templates/AcademicReportExportPanel";
import { academicExportRoute, academicRoute } from "@/components/academic/academicRoutes";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

/**
 * Issue #1267 (export slice) — route that connects the template gallery (#1265)
 * to saved research reports.
 *
 * The page only renders export controls around already-saved content; the session
 * is verified on the server before anything private appears, and the page is
 * excluded from indexing and archiving like the academic studio itself, because a
 * student's unpublished report is private work.
 */
export const dynamic = "force-dynamic";

export const metadata: Metadata = {
  robots: {
    index: false,
    follow: false,
    noarchive: true,
    nosnippet: true,
  },
};

export default async function AcademicReportExportPage({
  searchParams,
}: {
  searchParams: Promise<{ job?: string }>;
}) {
  await requireAuthenticatedPage(academicExportRoute);
  const { job } = await searchParams;
  const initialJobId = job && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(job) ? job : null;
  return (
    <ProtectedPage>
      <AcademicReportExportPanel initialJobId={initialJobId} />
      <p style={{ marginTop: 16, fontSize: 13 }}>
        <a href={academicRoute}>← Academic Research Studio</a>
      </p>
    </ProtectedPage>
  );
}
