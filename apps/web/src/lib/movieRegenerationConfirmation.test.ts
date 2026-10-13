import { describe, expect, it } from "vitest";
import type { MovieRegenerationCostPreview, MovieSelectiveRegenerationResponse } from "@/lib/api";
import { buildMovieRegenerationConfirmation, confirmMovieRegenerationIfAllowed, formatMovieRegenerationCost, movieRegenerationAttemptIsCurrent, retireMovieRegenerationAttempt, type MovieRegenerationAttempt } from "./movieRegenerationConfirmation";

function response(preview: MovieRegenerationCostPreview): MovieSelectiveRegenerationResponse {
  return { request: { id: "regen-1", costPreview: preview } } as MovieSelectiveRegenerationResponse;
}

function preview(overrides: Partial<MovieRegenerationCostPreview> = {}): MovieRegenerationCostPreview {
  return {
    estimatedProviderCostUsd: 1.25,
    estimatedProviderCostKnown: true,
    currency: "USD",
    costEstimateJson: null,
    confirmationRequired: true,
    guardrails: { estimateStatus: "known", estimatedProviderCostUsd: 1.25, estimatedProviderCostKnown: true, currency: "USD", unknownReason: null, warning: false, confirmationRequired: true, canProceed: true, workspaceCap: null, userCap: null, projectCap: null, warnings: [] },
    ...overrides,
  };
}

describe("Movie regeneration confirmation", () => {
  it("does not call the queue endpoint before explicit confirmation", async () => {
    const state = buildMovieRegenerationConfirmation(response(preview()));
    const calls: string[] = [];

    expect(calls).toEqual([]);
    expect(state.canConfirm).toBe(true);
    await confirmMovieRegenerationIfAllowed(state, (requestId) => {
      calls.push(requestId);
      return Promise.resolve();
    });

    expect(calls).toEqual(["regen-1"]);
  });

  it("fails closed when the server does not affirm readiness", async () => {
    const state = buildMovieRegenerationConfirmation(response(preview({ guardrails: null })));
    const calls: string[] = [];

    const confirmed = await confirmMovieRegenerationIfAllowed(state, async (requestId) => { calls.push(requestId); });

    expect(confirmed).toBe(false);
    expect(state.blockedReason).toBe("readiness-unavailable");
    expect(calls).toEqual([]);
  });

  it("keeps a known estimate and formats an unknown estimate without inventing a price", () => {
    expect(formatMovieRegenerationCost(preview(), "en")).toContain("1.25");
    expect(formatMovieRegenerationCost(preview({ estimatedProviderCostUsd: null, estimatedProviderCostKnown: false, minimumEstimatedProviderCostUsd: null, maximumEstimatedProviderCostUsd: null }), "ar")).toBeNull();
    expect(formatMovieRegenerationCost(preview({ estimatedProviderCostUsd: null, estimatedProviderCostKnown: false, minimumEstimatedProviderCostUsd: 1, maximumEstimatedProviderCostUsd: 2 }), "ku")).toContain("US$");
  });

  it("allows the identical confirmation retry after a transient network failure", async () => {
    const state = buildMovieRegenerationConfirmation(response(preview()));
    const calls: string[] = [];
    let failed = true;

    await expect(confirmMovieRegenerationIfAllowed(state, async (requestId) => {
      calls.push(requestId);
      if (failed) {
        failed = false;
        throw new Error("network unavailable");
      }
    })).rejects.toThrow("network unavailable");
    await expect(confirmMovieRegenerationIfAllowed(state, async (requestId) => { calls.push(requestId); })).resolves.toBe(true);

    expect(calls).toEqual(["regen-1", "regen-1"]);
  });

  it("rejects a late callback from a cancelled or replaced attempt", () => {
    const first: MovieRegenerationAttempt = { key: "shot-1:regenerate", generation: 1, keys: { create: "create-1", confirm: "confirm-1" } };
    const replacement: MovieRegenerationAttempt = { key: first.key, generation: 2, keys: { create: "create-2", confirm: "confirm-2" } };

    expect(movieRegenerationAttemptIsCurrent(first, first)).toBe(true);
    expect(movieRegenerationAttemptIsCurrent(undefined, first)).toBe(false);
    expect(movieRegenerationAttemptIsCurrent(replacement, first)).toBe(false);
    expect(movieRegenerationAttemptIsCurrent(replacement, replacement)).toBe(true);
    expect(retireMovieRegenerationAttempt(first, first)).toBeUndefined();
    expect(retireMovieRegenerationAttempt(replacement, first)).toBe(replacement);
  });
});
