import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("development learning guide route", () => {
  it("renders the guide behind both authentication gates and disables static caching", () => {
    expect(source).toContain('import { ProtectedPage } from "@/components/ProtectedPage";');
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage("/development/learn");');
    expect(source).toContain("return <ProtectedPage><DevelopmentLearningGuide /></ProtectedPage>");
  });
});
