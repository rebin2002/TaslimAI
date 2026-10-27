import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("private Movie project-module route", () => {
  it("renders the project workspace behind both server and client authentication gates", () => {
    expect(source).toContain('import { ProtectedPage } from "@/components/ProtectedPage";');
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain("await requireAuthenticatedPage(`/create/movie/${projectId}/${module}`);");
    expect(source).toContain("<ProtectedPage><FullMovieWorkspaceView projectId={projectId} module={module} /></ProtectedPage>");
  });
});
