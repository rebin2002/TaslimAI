import type { Metadata } from "next";
import { ProtectedPage } from "@/components/ProtectedPage";
import { AcademicResearchStudio } from "@/components/academic/AcademicResearchStudio";
import { academicRoute } from "@/components/academic/academicRoutes";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

/**
 * Issue #1259 (Academic Research Studio) — Phase A route.
 *
 * A sibling of `/create/research`, not a replacement: standard research keeps
 * its own page and its own state. The session is verified on the server before
 * any private planning content is rendered, matching the existing studio routes,
 * and the page is excluded from indexing and archiving because a student's
 * unpublished thesis plan is private work.
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

export default async function AcademicResearchStudioPage() {
  await requireAuthenticatedPage(academicRoute);
  return (
    <ProtectedPage>
      <AcademicResearchStudio />
    </ProtectedPage>
  );
}
