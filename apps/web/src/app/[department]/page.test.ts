import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("department root route authorization", () => {
  it("protects every known department root with server and client authentication", () => {
    expect(source).toContain('import { ProtectedPage } from "@/components/ProtectedPage";');
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain("await requireAuthenticatedPage(`/${department}`);");
    expect(source).toContain("return <ProtectedPage><PlaceholderPage nameKey={entry.titleKey} /></ProtectedPage>;");
  });
});
