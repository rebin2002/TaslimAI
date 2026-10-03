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
    expect(source).toContain('movieBody.world.referenceLibrary');
    expect(source).toContain('movieBody.world.openLibrary');
    expect(source).toContain('movieBody.world.referenceAsset');
    expect(source).toContain('movieBody.world.registered');
    expect(apiSource).toContain("getMovieWorld");
  });

  it("makes locks visible and preserves a conflict response instead of overwriting facts", () => {
    expect(source).toContain('movieDeep.lockedFacts');
    expect(source).toContain('movieBody.world.activeLocks');
    expect(source).toContain("The World record could not be saved.");
    expect(apiSource).toContain("updateMovieLocation");
    expect(apiSource).toContain("updateMovieSet");
    expect(apiSource).toContain("updateMovieProp");
  });

  it("surfaces location geography sheets before motion generation", () => {
    expect(source).toContain("Location geography sheet");
    expect(source).toContain("Establishing reference");
    expect(source).toContain("Wide 3/4 spatial reference");
    expect(source).toContain("Entrances / exits");
    expect(source).toContain("Orientation anchors");
    expect(source).toContain("Approve sheet");
    expect(apiSource).toContain("upsertMovieLocationGeographySheet");
    expect(apiSource).toContain("approveMovieLocationGeographyVariant");
  });
});
