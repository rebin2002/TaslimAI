import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Music Studio route authentication boundary", () => {
  it("checks the session on the server before rendering the private studio", () => {
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage("/create/music");');
    expect(source).toContain("export default async function MusicStudioPage()");
  });
});
