import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Presentation Studio route authentication boundary", () => {
  it("keeps the private studio behind server and client authentication", () => {
    expect(source).toContain('import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage(internalReturnPath("/create/presentation", query));');
    expect(source).toContain("return <ProtectedPage><PresentationStudioView /></ProtectedPage>");
  });

  it("keeps the studio selection promised to a signed-out visitor", () => {
    // PresentationStudioView selects its project from ?projectId, so the gate
    // must read the query before deciding the return path.
    expect(source).toContain("const query = await searchParams;");
    expect(source.indexOf("const query = await searchParams;")).toBeLessThan(
      source.indexOf('await requireAuthenticatedPage(internalReturnPath("/create/presentation", query));'),
    );
  });
});
