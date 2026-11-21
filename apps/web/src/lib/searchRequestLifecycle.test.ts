import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
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

  it("keeps the query-change invalidation token current until the debounced request starts", () => {
    const sequence = createRequestSequencer();
    const previousRequest = sequence.begin();
    const queryChange = sequence.begin();

    expect(sequence.isCurrent(previousRequest)).toBe(false);
    expect(sequence.isCurrent(queryChange)).toBe(true);
  });

  it("invalidates stale search responses before URL-query debounce scheduling", () => {
    const source = readFileSync(new URL("../components/GlobalSearchView.tsx", import.meta.url), "utf8");
    const effectStart = source.indexOf("  useEffect(() => {", source.indexOf("const runSearch"));
    const effectEnd = source.indexOf("  }, [clearPendingSearch, initialQuery, requestSequence, runSearch]);", effectStart);
    expect(effectStart).toBeGreaterThanOrEqual(0);
    expect(effectEnd).toBeGreaterThan(effectStart);
    const effect = source.slice(effectStart, effectEnd);

    expect(effect.indexOf("requestSequence.begin();")).toBeGreaterThanOrEqual(0);
    expect(effect.indexOf("requestSequence.begin();")).toBeLessThan(effect.indexOf("pendingSearchTimer.current = window.setTimeout"));
  });
});
