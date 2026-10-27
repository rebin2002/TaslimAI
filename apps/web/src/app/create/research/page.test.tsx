import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("Research Studio route authentication boundary", () => {
  it("checks the session on the server before rendering the client studio", () => {
    const route = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

    expect(route).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(route).toContain('export const dynamic = "force-dynamic";');
    expect(route).toContain('await requireAuthenticatedPage("/create/research");');
    expect(route).toContain("export default async function ResearchStudioPage()");
  });
});
