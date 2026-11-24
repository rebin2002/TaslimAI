import { beforeEach, describe, expect, it, vi } from "vitest";
import { MAX_ADVERTISED_RETRY_AFTER_SECONDS, MAX_THROTTLE_RETRY_WAIT_MS, parseRetryAfterSeconds } from "./apiRetryAfter";

/**
 * Retry-After contract for the browser API client.
 *
 * The API answers a throttled read with `429`/`RATE_LIMITED` (and an
 * unavailable route with `503`) plus an RFC 9110 `Retry-After` hint that says
 * when this very request may run again. These tests pin what the client does
 * with that hint: an idempotent GET is repeated once, no earlier than the
 * server allowed, and a request that already created state is never repeated.
 */

const rateLimitedBody = { error: { code: "RATE_LIMITED", message: "Too many requests. Please try again later." } };
const unavailableBody = { error: { code: "API_UNAVAILABLE", message: "The service is temporarily unavailable. Please try again." } };
const searchBody = { query: "report", totalCount: 1, page: 1, limit: 8, hasMore: false, groups: [] };

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...headers },
  });
}

function throttledResponse(status: number, retryAfter: string | null, body = rateLimitedBody) {
  const headers: Record<string, string> = {};
  if (retryAfter !== null) headers["Retry-After"] = retryAfter;
  return jsonResponse(body, status, headers);
}

async function flushMicrotasks() {
  for (let index = 0; index < 12; index += 1) await Promise.resolve();
}

async function clientWith(fetchMock: ReturnType<typeof vi.fn>) {
  vi.stubGlobal("fetch", fetchMock);
  return await import("./api");
}

describe("parseRetryAfterSeconds", () => {
  const nowMs = Date.parse("2026-11-17T04:00:00Z");

  it("reads a delta-seconds hint", () => {
    expect(parseRetryAfterSeconds("2", nowMs)).toBe(2);
    expect(parseRetryAfterSeconds(" 45 ", nowMs)).toBe(45);
    expect(parseRetryAfterSeconds("0", nowMs)).toBe(0);
  });

  it("reads an HTTP-date hint relative to the clock", () => {
    const future = new Date(nowMs + 4_000).toUTCString();
    expect(parseRetryAfterSeconds(future, nowMs)).toBe(4);
    // A hint that has already expired is due now, not never.
    const past = new Date(nowMs - 60_000).toUTCString();
    expect(parseRetryAfterSeconds(past, nowMs)).toBe(0);
  });

  it("rejects anything the client must not guess about", () => {
    for (const value of [null, undefined, "", "   ", "soon", "-5", "+5", "1.5", "5s", "1e3", "NaN", "Tue, 32 Nov 2026 04:00:00 GMT"]) {
      expect(parseRetryAfterSeconds(value, nowMs)).toBeNull();
    }
  });

  it("rejects an advertised wait beyond the defensive ceiling", () => {
    expect(MAX_ADVERTISED_RETRY_AFTER_SECONDS).toBeLessThan(Number.MAX_SAFE_INTEGER);
    expect(parseRetryAfterSeconds("86401", nowMs)).toBeNull();
    expect(parseRetryAfterSeconds("86400", nowMs)).toBe(86400);
  });

  it("keeps the automatic wait ceiling inside the advertised ceiling", () => {
    expect(MAX_THROTTLE_RETRY_WAIT_MS).toBeLessThanOrEqual(MAX_ADVERTISED_RETRY_AFTER_SECONDS * 1000);
  });
});

describe("API client Retry-After handling", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  it("repeats a throttled read once the advertised delay has passed", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(throttledResponse(429, "2"))
      .mockResolvedValueOnce(jsonResponse(searchBody));
    const { api } = await clientWith(fetchMock);

    const pending = api.search("report");
    await flushMicrotasks();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // The hint is honoured, not guessed at: nothing is sent before 2 seconds.
    await vi.advanceTimersByTimeAsync(1_999);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(1);
    await expect(pending).resolves.toMatchObject({ totalCount: 1 });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect((fetchMock.mock.calls[1][1] as RequestInit | undefined)?.method ?? "GET").toBe("GET");
    expect(String(fetchMock.mock.calls[1][0])).toContain("/api/search?q=report&page=1&limit=8");
  });

  it("repeats an unavailable read on a gateway hint", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(throttledResponse(503, "3", unavailableBody))
      .mockResolvedValueOnce(jsonResponse(searchBody));
    const { api } = await clientWith(fetchMock);

    const pending = api.search("report");
    await vi.advanceTimersByTimeAsync(3_000);
    await expect(pending).resolves.toMatchObject({ query: "report" });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("repeats only once, so a still-throttled read fails like it did before", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(throttledResponse(429, "1"))
      .mockResolvedValueOnce(throttledResponse(429, "1"));
    const { ApiError, api } = await clientWith(fetchMock);

    const settled = api.search("report").catch((error) => error);
    await vi.advanceTimersByTimeAsync(30_000);
    const failure = await settled;

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.status).toBe(429);
    expect(failure.code).toBe("RATE_LIMITED");
    expect(failure.message).toBe("Too many requests. Please try again later.");
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("surfaces the shipped 60-second instruction instead of hanging the read on it", async () => {
    // `RateLimiting.OnRejected` publishes `Retry-After: 60`, which is far past
    // the budget one interactive read may spend waiting inside a promise.
    const fetchMock = vi.fn().mockResolvedValueOnce(throttledResponse(429, "60"));
    const { ApiError, api } = await clientWith(fetchMock);

    const failure = await api.search("report").catch((error) => error);
    await vi.advanceTimersByTimeAsync(120_000);

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.status).toBe(429);
    expect(failure.code).toBe("RATE_LIMITED");
    // The server instruction survives the failure instead of being discarded,
    // so the caller can act on it rather than re-fetching on a blind guess.
    expect(failure.retryAfterSeconds).toBe(60);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("never resubmits a state-changing request, whatever the hint says", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ token: "csrf-token" }))
      .mockResolvedValueOnce(throttledResponse(429, "1"));
    const { ApiError, api } = await clientWith(fetchMock);

    const settled = api.login({ email: "owner@example.com", password: "StrongPassword!123" }).catch((error) => error);
    await vi.advanceTimersByTimeAsync(30_000);
    const failure = await settled;

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.status).toBe(429);
    expect(failure.retryAfterSeconds).toBe(1);
    // The POST was never re-sent, so no authentication attempt was duplicated.
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect((fetchMock.mock.calls[1][1] as RequestInit).method).toBe("POST");
  });

  it("keeps a hint-less or malformed throttle on the existing failure path", async () => {
    for (const retryAfter of [null, "soon", "-5", "1.5"]) {
      vi.resetModules();
      const fetchMock = vi.fn().mockResolvedValueOnce(throttledResponse(429, retryAfter));
      const { ApiError, api } = await clientWith(fetchMock);

      const failure = await api.search("report").catch((error) => error);
      await vi.advanceTimersByTimeAsync(30_000);

      expect(failure).toBeInstanceOf(ApiError);
      expect(failure.status).toBe(429);
      expect(failure.retryAfterSeconds).toBeUndefined();
      expect(fetchMock).toHaveBeenCalledTimes(1);
    }
  });

  it("stops waiting when the caller aborts a superseded read", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(throttledResponse(429, "8"));
    const { api } = await clientWith(fetchMock);

    const controller = new AbortController();
    const settled = api.listProjects("workspace-1", "Active", controller.signal).catch((error) => error);
    await flushMicrotasks();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    controller.abort();
    const failure = await settled;
    await vi.advanceTimersByTimeAsync(60_000);

    expect(failure).toBeInstanceOf(DOMException);
    expect(failure.name).toBe("AbortError");
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("abandons a queued repeat when the caller aborts before it is due", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(throttledResponse(429, "9"));
    const { api } = await clientWith(fetchMock);

    const controller = new AbortController();
    const settled = api.listProjects("workspace-1", "Active", controller.signal).catch((error) => error);
    await flushMicrotasks();
    await vi.advanceTimersByTimeAsync(5_000);

    controller.abort();
    const failure = await settled;
    await vi.advanceTimersByTimeAsync(30_000);

    expect(failure).toBeInstanceOf(DOMException);
    expect(failure.name).toBe("AbortError");
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("leaves a successful read and its single-fetch budget untouched", async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(jsonResponse(searchBody));
    const { api } = await clientWith(fetchMock);

    await expect(api.search("report")).resolves.toMatchObject({ totalCount: 1 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
