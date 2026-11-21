import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./DepartmentSection.tsx", import.meta.url), "utf8");

describe("homepage department navigation accessibility", () => {
  it("does not add a keyboard focus stop to the wrapper around feature links", () => {
    expect(source).toContain('className="feature-row"');
    expect(source).toContain('<Link key={feature.id}');
    expect(source).not.toContain('className="feature-row" tabIndex={0}');
  });

  it("marks the feature-link wrapper with a role so its accessible name is announced", () => {
    const row = /<div\s+className="feature-row"([^>]*)>/.exec(source);
    expect(row).not.toBeNull();
    expect(row?.[1]).toContain('role="group"');
  });
});
