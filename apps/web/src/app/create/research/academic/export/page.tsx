import type { Metadata } from "next";
import { ProtectedPage } from "@/components/ProtectedPage";
import { AcademicReportExportPanel } from "@/components/academic/templates/AcademicReportExportPanel";
import { academicExportRoute, academicRoute } from "@/components/academic/academicRoutes";
import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";

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
  searchParams: Promise<{ job?: string | string[] }>;
}) {
  // Read the requested location first. `?job=` selects which saved report the panel opens, and the
  // gate below hands the reader back exactly what they asked for; a pathname-only return path would
  // land an expired session on an empty export screen and lose the deep link mid-action. Same
  // server-gate pattern as `/assets` and `/account/generation-jobs`, so the promised destination
  // matches the browser-side boundary's instead of narrowing it.
  const query = await searchParams;
  await requireAuthenticatedPage(internalReturnPath(academicExportRoute, query));
  // Validation stays where the value is consumed: the identifier is checked before it reaches the
  // panel, so a malformed parameter survives sign-in as text and is then refused as data.
  const job = Array.isArray(query.job) ? query.job[0] : query.job;
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
