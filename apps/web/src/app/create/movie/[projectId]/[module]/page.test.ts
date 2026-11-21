import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");
describe("Full movie module route authorization", () => {
  it("checks the session on the server and disables static caching for the private movie workspace", () => {
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain("await requireAuthenticatedPage(`/create/movie/${projectId}/${module}`);");
    expect(source).toContain("<ProtectedPage><FullMovieWorkspaceView projectId={projectId} module={module} /></ProtectedPage>");
  });
  it("verifies the session before the protected shell is rendered", () => {
    const gate = source.indexOf("await requireAuthenticatedPage(");
    const render = source.indexOf("<ProtectedPage>");
    expect(gate).toBeGreaterThan(-1);
    expect(render).toBeGreaterThan(gate);
  });
});
