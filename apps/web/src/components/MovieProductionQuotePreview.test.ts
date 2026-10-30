import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const workspaceSource = readFileSync(new URL("./MovieProductionWorkspace.tsx", import.meta.url), "utf8");
const legacyWorkspaceSource = readFileSync(new URL("./FullMovieWorkspaceView.tsx", import.meta.url), "utf8");
const apiSource = readFileSync(new URL("../lib/api.ts", import.meta.url), "utf8");

describe("Movie production quote confirmation", () => {
  it("previews before render, displays the maximum USD amount and binds the exact quote on confirmation", () => {
    expect(workspaceSource).toContain("api.getMovieProductionPreflight(shotId");
    expect(workspaceSource).toContain("preview.priceIntent");
    expect(workspaceSource).toContain("Maximum estimated price:");
    expect(workspaceSource).toContain("Confirm and queue");
    expect(workspaceSource).toContain("expectedMaximumAmountUsd: priceIntent.expectedMaximumAmountUsd");
    expect(workspaceSource).toContain("expectedQuoteFingerprint: priceIntent.quoteFingerprint");
    expect(workspaceSource).toContain("confirmationAccepted: true");
  });

  it("keeps the legacy production callback quote-bound and requires the same fields in the API client", () => {
    expect(legacyWorkspaceSource).toContain("api.getMovieProductionPreflight(shot.id");
    expect(legacyWorkspaceSource).toContain("window.confirm(`The maximum estimated price is ${amount}.");
    expect(apiSource).toContain("expectedMaximumAmountUsd: number; expectedQuoteFingerprint: string");
    expect(apiSource).toContain("confirmationAccepted: true");
  });
});
