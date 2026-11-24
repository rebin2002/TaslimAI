import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("private memory route", () => {
  it("renders personal memory behind both server and client authentication gates", () => {
    expect(source).toContain('import { ProtectedPage } from "@/components/ProtectedPage";');
    expect(source).toContain('import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage(internalReturnPath("/account/memory", query));');
    expect(source).toContain("<ProtectedPage><MemoryView /></ProtectedPage>");
  });

  it("promises back the requested location rather than only the pathname", () => {
    // The client boundary this gate fronts returns pathname + search, so the
    // server gate must not narrow that promised destination.
    expect(source).toContain("const query = await searchParams;");
    expect(source.indexOf("const query = await searchParams;")).toBeLessThan(
      source.indexOf('await requireAuthenticatedPage(internalReturnPath("/account/memory", query));'),
    );
  });
});
