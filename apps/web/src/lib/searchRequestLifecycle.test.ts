import { describe, expect, it } from "vitest";
import { createRequestSequencer } from "./searchRequestLifecycle";

describe("createRequestSequencer", () => {
  it("accepts the newest request and fences an older response", () => {
    const sequence = createRequestSequencer();
    const firstRequest = sequence.begin();
    const secondRequest = sequence.begin();

    expect(sequence.isCurrent(firstRequest)).toBe(false);
    expect(sequence.isCurrent(secondRequest)).toBe(true);
  });

  it("invalidates an in-flight request when a reset begins", () => {
    const sequence = createRequestSequencer();
    const request = sequence.begin();

    sequence.begin();

    expect(sequence.isCurrent(request)).toBe(false);
  });
});
