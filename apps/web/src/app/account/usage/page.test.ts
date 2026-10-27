import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("account Usage route", () => {
  it("protects usage data with server and client authentication without static caching", () => {
    expect(source).toContain('import { ProtectedPage } from "@/components/ProtectedPage";');
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage("/account/usage");');
    expect(source).toContain("<ProtectedPage><UsageView /></ProtectedPage>");
  });
});
