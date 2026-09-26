import { describe, expect, it } from "vitest";
import { canMovieAction, movieOperationalActions, type MovieCapabilityResponse } from "./api";

describe("Movie capability contract", () => {
  it("uses server action booleans without inferring unrelated permissions", () => {
    const reviewer: MovieCapabilityResponse = {
      movieProjectId: "movie-1",
      permissions: ["View", "Comment", "Approve"],
      capabilities: {
        [movieOperationalActions.comments]: true,
        [movieOperationalActions.reviewsRequest]: true,
        [movieOperationalActions.reviewsDecision]: true,
        [movieOperationalActions.generate]: false,
        [movieOperationalActions.teamManagement]: false,
        [movieOperationalActions.budgetManagement]: false,
      },
    };

    expect(canMovieAction(reviewer, movieOperationalActions.comments)).toBe(true);
    expect(canMovieAction(reviewer, movieOperationalActions.reviewsDecision)).toBe(true);
    expect(canMovieAction(reviewer, movieOperationalActions.generate)).toBe(false);
    expect(canMovieAction(reviewer, movieOperationalActions.teamManagement)).toBe(false);
    expect(canMovieAction(reviewer, movieOperationalActions.budgetManagement)).toBe(false);
  });

  it("fails closed while the capability snapshot is unavailable", () => {
    expect(canMovieAction(null, movieOperationalActions.generate)).toBe(false);
    expect(canMovieAction(undefined, movieOperationalActions.teamManagement)).toBe(false);
  });
});
