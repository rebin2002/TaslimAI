import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Generation Jobs route", () => {
  it("renders the private jobs view behind both authentication gates without static caching", () => {
    expect(source).toContain('import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage(internalReturnPath("/account/generation-jobs", query));');
    expect(source).toContain("<ProtectedPage><GenerationJobsView /></ProtectedPage>");
  });

  it("promises back the requested location rather than only the pathname", () => {
    expect(source).toContain("const query = await searchParams;");
    expect(source.indexOf("const query = await searchParams;")).toBeLessThan(
      source.indexOf('await requireAuthenticatedPage(internalReturnPath("/account/generation-jobs", query));'),
    );
  });
});
