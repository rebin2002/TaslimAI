import { describe, expect, it } from "vitest";
import { shouldApplyProjectAssetsResponse } from "./ProjectAssetsSection";

describe("ProjectAssetsSection request fencing", () => {
  it("applies only the active project request response", () => {
    expect(shouldApplyProjectAssetsResponse(true, 2, 2)).toBe(true);
    expect(shouldApplyProjectAssetsResponse(true, 1, 2)).toBe(false);
    expect(shouldApplyProjectAssetsResponse(false, 2, 2)).toBe(false);
  });
});
