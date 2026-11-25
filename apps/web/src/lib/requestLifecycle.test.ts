import { describe, expect, it } from "vitest";
import { createRequestGuard, isAbortError } from "./requestLifecycle";

describe("request lifecycle guards", () => {
  it("aborts the previous request and fences its late result", () => {
    const guard = createRequestGuard();
    const first = guard.begin();
    const second = guard.begin();

    expect(first.signal.aborted).toBe(true);
    expect(first.isCurrent()).toBe(false);
    expect(second.signal.aborted).toBe(false);
    expect(second.isCurrent()).toBe(true);
  });

  it("cancels the active request on unmount or route cleanup", () => {
    const guard = createRequestGuard();
    const request = guard.begin();

    guard.cancel();

    expect(request.signal.aborted).toBe(true);
    expect(request.isCurrent()).toBe(false);
  });

  it("recognizes DOM-style abort errors without matching ordinary failures", () => {
    expect(isAbortError(Object.assign(new Error("cancelled"), { name: "AbortError" }))).toBe(true);
    expect(isAbortError(new Error("network down"))).toBe(false);
    expect(isAbortError(null)).toBe(false);
  });
});
