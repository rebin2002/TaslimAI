import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Movie creation route authorization", () => {
  it("checks the session on the server and disables static caching for the private studio entry", () => {
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage("/create/movie");');
    expect(source).toContain("<ProtectedPage><MovieStudioView /></ProtectedPage>");
  });
});
