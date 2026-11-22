import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("private admin usage route", () => {
  it("renders usage and charge telemetry behind server and client authentication gates", () => {
    expect(source).toContain('import { AdminPage } from "@/components/AdminPage";');
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage("/account/admin/usage");');
    expect(source).toContain("<AdminPage><AdminUsageView /></AdminPage>");
  });
});
