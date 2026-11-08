import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Movie workspace route", () => {
  it("requires a server-verified session before rendering and disables static caching", () => {
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain("await requireAuthenticatedPage(`/create/movie/${projectId}/${module}`);");

    const authenticationCheck = source.indexOf("await requireAuthenticatedPage(");
    const workspaceRender = source.indexOf("return <ProtectedPage><FullMovieWorkspaceView");
    expect(authenticationCheck).toBeGreaterThan(-1);
    expect(workspaceRender).toBeGreaterThan(authenticationCheck);
  });
});
