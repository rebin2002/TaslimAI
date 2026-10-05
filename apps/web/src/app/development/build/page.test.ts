import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("development build route", () => {
  it("renders the build checklist behind the shared protected-page gate", () => {
    expect(source).toContain("<ProtectedPage><DevelopmentBuildChecklist /></ProtectedPage>");
  });
});
