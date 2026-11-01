import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Presentation Studio route authentication boundary", () => {
  it("keeps the private studio behind server and client authentication", () => {
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage("/create/presentation");');
    expect(source).toContain("return <ProtectedPage><PresentationStudioView /></ProtectedPage>");
  });
});
