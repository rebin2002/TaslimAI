import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("Billing route authorization", () => {
  it("checks the session on the server before rendering private billing data", () => {
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage("/account/billing");');
    expect(source).toContain("return <ProtectedPage><BillingView /></ProtectedPage>");
  });
});
