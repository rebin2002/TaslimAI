import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Admin Operations route", () => {
  it("renders the private operations dashboard behind server auth and the admin client gate", () => {
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage("/account/admin/operations");');
    expect(source).toContain("<AdminPage><AdminOperationsView /></AdminPage>");
  });
});
