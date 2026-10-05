import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("development build route", () => {
  it("renders the build checklist behind both authentication gates without static caching", () => {
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage("/development/build");');
    expect(source).toContain("<ProtectedPage><DevelopmentBuildChecklist /></ProtectedPage>");
  });
});
