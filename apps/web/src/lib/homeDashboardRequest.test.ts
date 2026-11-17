import { describe, expect, it } from "vitest";
import { isHomeDashboardRequestCurrent, startHomeDashboardRequest } from "./homeDashboardRequest";

// Deterministic synthetic coverage for the home dashboard request fence: fixed
// sequences and workspace ids only, no timers, no network, no provider.
const PERSONAL = "workspace-personal";
const BUSINESS = "workspace-business";

describe("homeDashboardRequest", () => {
  it("numbers each load ahead of the one it replaces", () => {
    expect(startHomeDashboardRequest(0, PERSONAL).sequence).toBe(1);
    expect(startHomeDashboardRequest(1, BUSINESS).sequence).toBe(2);
  });

  it("keeps the workspace the load was started for", () => {
    expect(startHomeDashboardRequest(3, BUSINESS)).toEqual({ sequence: 4, workspaceId: BUSINESS });
  });

  it("accepts the newest load of the mounted workspace", () => {
    const request = startHomeDashboardRequest(5, BUSINESS);
    expect(isHomeDashboardRequestCurrent(request, request.sequence, BUSINESS)).toBe(true);
  });

  it("rejects a superseded load even while the same workspace stays mounted", () => {
    const retired = startHomeDashboardRequest(5, BUSINESS);
    const replacement = startHomeDashboardRequest(retired.sequence, BUSINESS);
    expect(isHomeDashboardRequestCurrent(retired, replacement.sequence, BUSINESS)).toBe(false);
    expect(isHomeDashboardRequestCurrent(replacement, replacement.sequence, BUSINESS)).toBe(true);
  });

  it("rejects a load whose workspace is no longer mounted", () => {
    const request = startHomeDashboardRequest(0, PERSONAL);
    expect(isHomeDashboardRequestCurrent(request, request.sequence, BUSINESS)).toBe(false);
  });

  it("rejects any load once no workspace is mounted", () => {
    // Signing out starts no replacement load, so the sequence alone would still
    // match; the workspace identity is what retires the in-flight snapshot.
    const request = startHomeDashboardRequest(1, PERSONAL);
    expect(isHomeDashboardRequestCurrent(request, 1, null)).toBe(false);
    expect(isHomeDashboardRequestCurrent(request, 2, null)).toBe(false);
  });

  it("rejects a retired load when nothing newer was started", () => {
    // Unmounting and workspace changes bump the sequence without starting a
    // load, which is how an in-flight reply is dropped without publishing.
    const request = startHomeDashboardRequest(0, PERSONAL);
    expect(isHomeDashboardRequestCurrent(request, 1, PERSONAL)).toBe(true);
    expect(isHomeDashboardRequestCurrent(request, 2, PERSONAL)).toBe(false);
  });

  it("never revives a token by comparing it against a stale sequence", () => {
    const first = startHomeDashboardRequest(0, PERSONAL);
    const second = startHomeDashboardRequest(first.sequence, PERSONAL);
    expect(isHomeDashboardRequestCurrent(first, first.sequence, PERSONAL)).toBe(true);
    expect(isHomeDashboardRequestCurrent(second, first.sequence, PERSONAL)).toBe(false);
  });
});
