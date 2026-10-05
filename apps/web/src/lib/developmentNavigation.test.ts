import { describe, expect, it } from "vitest";
import { developmentNavigation } from "./developmentNavigation";

describe("development navigation", () => {
  it("exposes every development feature at its canonical route", () => {
    expect(developmentNavigation.map((item) => item.id)).toEqual(["learn", "build", "code", "debug", "projects"]);
    expect(developmentNavigation.map((item) => item.href)).toEqual([
      "/development/learn",
      "/development/build",
      "/development/code",
      "/development/debug",
      "/development/projects",
    ]);
  });

  it("does not duplicate destinations or lose feature labels", () => {
    expect(new Set(developmentNavigation.map((item) => item.href)).size).toBe(developmentNavigation.length);
    expect(developmentNavigation.every((item) => item.labelKey.startsWith("feature."))).toBe(true);
  });
});
