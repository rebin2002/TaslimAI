/**
 * Issue #1259 (Academic Research Studio) — route boundary tests.
 *
 * Follows the existing studio-route convention: the auth contract of the page
 * module is pinned from its source. A student's unpublished thesis plan is
 * private work, so the route must be server-gated and excluded from indexing
 * and archiving. The recovery boundary is covered in `error.test.tsx`.
*/
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("Academic Research Studio route authentication boundary", () => {
  const route = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

  it("checks the session on the server before rendering the academic studio", () => {
    expect(route).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(route).toContain('export const dynamic = "force-dynamic";');
    expect(route).toContain("await requireAuthenticatedPage(academicRoute);");
    expect(route).toContain('import { academicRoute } from "@/components/academic/academicRoutes";');
    expect(route).toContain("export default async function AcademicResearchStudioPage()");
    expect(route).toContain("<ProtectedPage>");
    expect(route).toContain("<AcademicResearchStudio />");
  });

  it("is a sibling route that leaves standard research in place", () => {
    expect(route).toContain("Academic");
    // The academic page must not render or import the standard studio view.
    expect(route).not.toContain("ResearchStudioView");
  });

  it("keeps private planning work out of search engines and archives", () => {
    expect(route).toMatch(/index:\s*false/);
    expect(route).toMatch(/follow:\s*false/);
    expect(route).toMatch(/noarchive:\s*true/);
  });
});
