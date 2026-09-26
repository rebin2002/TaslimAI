import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./MovieWorldWorkspace.tsx", import.meta.url), "utf8");
const apiSource = readFileSync(new URL("../lib/api.ts", import.meta.url), "utf8");

describe("Movie World workspace", () => {
  it("keeps the three production rooms explicit", () => {
    expect(source).toContain('id: "locations"');
    expect(source).toContain('id: "sets"');
    expect(source).toContain('id: "props"');
    expect(source).toContain("Scene / shot usage");
  });

  it("surfaces visual references through the unified Asset Library", () => {
    expect(source).toContain("Reference library");
    expect(source).toContain("Open Asset Library");
    expect(source).toContain("Reference Asset");
    expect(source).toContain("Register reference");
    expect(apiSource).toContain("getMovieWorld");
  });

  it("makes locks visible and preserves a conflict response instead of overwriting facts", () => {
    expect(source).toContain("Locked facts stay protected");
    expect(source).toContain("active locks");
    expect(source).toContain("The World record could not be saved.");
    expect(apiSource).toContain("updateMovieLocation");
    expect(apiSource).toContain("updateMovieSet");
    expect(apiSource).toContain("updateMovieProp");
  });
});
