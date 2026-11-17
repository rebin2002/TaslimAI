import { describe, expect, it } from "vitest";
import { nextStudioChoiceIndex } from "./studioChoiceNavigation";

// The seven Image Studio purpose presets, as rendered in dictionary order.
const PURPOSE_COUNT = 7;

describe("nextStudioChoiceIndex", () => {
  it("steps forward and backward through the option list", () => {
    expect(nextStudioChoiceIndex(0, "ArrowRight", PURPOSE_COUNT)).toBe(1);
    expect(nextStudioChoiceIndex(0, "ArrowDown", PURPOSE_COUNT)).toBe(1);
    expect(nextStudioChoiceIndex(3, "ArrowLeft", PURPOSE_COUNT)).toBe(2);
    expect(nextStudioChoiceIndex(3, "ArrowUp", PURPOSE_COUNT)).toBe(2);
  });

  it("wraps around both ends so a radio group never dead-ends", () => {
    expect(nextStudioChoiceIndex(PURPOSE_COUNT - 1, "ArrowRight", PURPOSE_COUNT)).toBe(0);
    expect(nextStudioChoiceIndex(0, "ArrowLeft", PURPOSE_COUNT)).toBe(PURPOSE_COUNT - 1);
  });

  it("jumps to the first and last option with Home and End", () => {
    expect(nextStudioChoiceIndex(4, "Home", PURPOSE_COUNT)).toBe(0);
    expect(nextStudioChoiceIndex(4, "End", PURPOSE_COUNT)).toBe(PURPOSE_COUNT - 1);
    expect(nextStudioChoiceIndex(0, "Home", PURPOSE_COUNT)).toBe(0);
    expect(nextStudioChoiceIndex(PURPOSE_COUNT - 1, "End", PURPOSE_COUNT)).toBe(PURPOSE_COUNT - 1);
  });

  it("leaves keys that are not choice navigation to the browser", () => {
    for (const key of ["Tab", "Enter", " ", "Escape", "PageDown", "ArrowUpArrow"]) {
      expect(nextStudioChoiceIndex(2, key, PURPOSE_COUNT), key).toBeNull();
    }
  });

  it("refuses to move when the current value is not in the group", () => {
    expect(nextStudioChoiceIndex(-1, "ArrowRight", PURPOSE_COUNT)).toBeNull();
    expect(nextStudioChoiceIndex(PURPOSE_COUNT, "ArrowRight", PURPOSE_COUNT)).toBeNull();
    expect(nextStudioChoiceIndex(0, "ArrowRight", 0)).toBeNull();
  });
});
