import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("development hub route", () => {
  it("renders the development hub behind the shared protected-page gate", () => {
    expect(source).toContain("<ProtectedPage><DevelopmentHub /></ProtectedPage>");
  });
});
