import { describe, expect, it } from "vitest";
import {
  isMovieExportTerminalStatus,
  movieExportCardState,
  movieExportProgressPercent,
  movieExportTerminalStatuses,
} from "./movieExportDeliveryState";
import { movieDeliveryKeys, movieDeliveryText } from "./movieDeliveryI18n";

// Synthetic state vocabulary only: the statuses come from the persisted assembly
// record, exactly as the API writes them (MovieAssemblyStatuses).
describe("movie export queue card state", () => {
  it("agrees with the API's terminal set", () => {
    expect([...movieExportTerminalStatuses].sort()).toEqual(
      ["Cancelled", "Failed", "Ready"].sort(),
    );
    expect(isMovieExportTerminalStatus("Cancelled")).toBe(true);
    expect(isMovieExportTerminalStatus("Assembling")).toBe(false);
    expect(isMovieExportTerminalStatus("Planned")).toBe(false);
  });

  it("never lets a cancelled master claim ongoing production", () => {
    const state = movieExportCardState("Cancelled");
    expect(state.isTerminal).toBe(true);
    expect(state.isProducing).toBe(false);
    expect(state.tone).toBe("is-failed");
    expect(state.icon).toBe("cancelled");
    expect(state.hintKey).toBe("cancelledOutput");
    expect(state.hintKey).not.toBe("stillProducing");
  });

  it("keeps the three terminal states distinguishable", () => {
    expect(movieExportCardState("Ready").icon).toBe("ready");
    expect(movieExportCardState("Failed").icon).toBe("failed");
    expect(movieExportCardState("Cancelled").icon).toBe("cancelled");
    expect(movieExportCardState("Failed").hintKey).toBe("failedOutput");
    expect(movieExportCardState("Cancelled").hintKey).toBe("cancelledOutput");
  });

  it("keeps a non-terminal record in the producing family", () => {
    for (const status of ["Planned", "Queued", "Assembling", "Unknown"]) {
      const state = movieExportCardState(status);
      expect(state.tone, status).toBe("is-pending");
      expect(state.isProducing, status).toBe(true);
      expect(state.icon, status).toBe("producing");
    }
  });

  it("states the frozen progress of a stopped master as a bounded value", () => {
    expect(movieExportProgressPercent(45)).toBe(45);
    expect(movieExportProgressPercent(-6)).toBe(0);
    expect(movieExportProgressPercent(112)).toBe(100);
    expect(movieExportProgressPercent(Number.NaN)).toBe(0);
    expect(movieExportProgressPercent(45.7)).toBe(45);
  });

  it("cites a catalog key that exists in every launch locale", () => {
    expect(movieDeliveryKeys).toContain("cancelledOutput");
    for (const locale of ["en", "ar", "ku"] as const) {
      const value = movieDeliveryText(locale, "cancelledOutput");
      expect(value).not.toBe("");
      expect(value).not.toContain("still being produced");
      if (locale !== "en") expect(value).not.toBe("cancelledOutput");
    }
  });
});
