import { beforeEach, describe, expect, it, vi } from "vitest";

const encoder = new TextEncoder();

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function csrfResponse(token: string) {
  return response({ token });
}

function streamResponse(reader: ReadableStreamDefaultReader<Uint8Array>) {
  return {
    ok: true,
    status: 200,
    body: { getReader: () => reader },
  } as unknown as Response;
}

function startedChunk() {
  return encoder.encode("event: message.started\ndata: {}\n\n");
}

function deltaChunk() {
  return encoder.encode("event: message.delta\ndata: {\"messageId\":\"assistant-1\",\"delta\":\"late\"}\n\n");
}

function completedChunk() {
  return encoder.encode("event: message.completed\ndata: {}\n\n");
}

async function flushMicrotasks() {
  for (let index = 0; index < 12; index += 1) await Promise.resolve();
}

describe("Chat stream transport", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  it("fails deterministically on an idle stream and ignores a late chunk", async () => {
    vi.useFakeTimers();
    let resolveLateRead!: (result: ReadableStreamReadResult<Uint8Array>) => void;
    const reader = {
      read: vi.fn(() => new Promise<ReadableStreamReadResult<Uint8Array>>(resolve => { resolveLateRead = resolve; })),
      cancel: vi.fn().mockResolvedValue(undefined),
      releaseLock: vi.fn(),
    } as unknown as ReadableStreamDefaultReader<Uint8Array>;
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(csrfResponse("token"))
      .mockResolvedValueOnce(streamResponse(reader));
    vi.stubGlobal("fetch", fetchMock);
    const { ApiError, api } = await import("./api");
    const events: string[] = [];

    const pending = api.streamMessage("conversation-1", "hello", event => events.push(event.type), "request-1");
    const failureResult = pending.catch(error => error);
    await flushMicrotasks();
    expect(reader.read).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(45_000);
    const failure = await failureResult;

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.code).toBe("STREAM_TIMEOUT");
    expect(failure.status).toBe(504);
    expect(events).toEqual([]);
    expect(reader.cancel).toHaveBeenCalledTimes(1);

    resolveLateRead({ done: false, value: deltaChunk() });
    await flushMicrotasks();
    expect(events).toEqual([]);
    vi.useRealTimers();
  });

  it("maps a reader connection failure to a safe retryable error", async () => {
    let readCount = 0;
    const reader = {
      read: vi.fn(() => {
        readCount += 1;
        return readCount === 1
          ? Promise.resolve({ done: false, value: startedChunk() })
          : Promise.reject(new Error("socket detail must stay private"));
      }),
      cancel: vi.fn().mockResolvedValue(undefined),
      releaseLock: vi.fn(),
    } as unknown as ReadableStreamDefaultReader<Uint8Array>;
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(csrfResponse("token"))
      .mockResolvedValueOnce(streamResponse(reader));
    vi.stubGlobal("fetch", fetchMock);
    const { ApiError, api } = await import("./api");

    const failure = await api.streamMessage("conversation-1", "hello", () => undefined, "request-2").catch(error => error);

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure.code).toBe("STREAM_CONNECTION_LOST");
    expect(failure.status).toBe(502);
    expect(failure.message).toBe("The chat stream connection was lost.");
    expect(reader.cancel).toHaveBeenCalledTimes(1);
  });

  it("settles successfully when the connection fails after a terminal event", async () => {
    let readCount = 0;
    const reader = {
      read: vi.fn(() => {
        readCount += 1;
        return readCount === 1
          ? Promise.resolve({ done: false, value: completedChunk() })
          : Promise.reject(new Error("socket closed after completion"));
      }),
      cancel: vi.fn().mockResolvedValue(undefined),
      releaseLock: vi.fn(),
    } as unknown as ReadableStreamDefaultReader<Uint8Array>;
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(csrfResponse("token"))
      .mockResolvedValueOnce(streamResponse(reader));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");
    const events: string[] = [];

    await expect(api.streamMessage("conversation-1", "hello", event => events.push(event.type), "request-terminal"))
      .resolves.toBeUndefined();

    expect(events).toEqual(["message.completed"]);
    expect(reader.cancel).toHaveBeenCalledTimes(1);
  });

  it("preserves a caller abort as cancellation instead of a watchdog failure", async () => {
    let resolveRead!: (result: ReadableStreamReadResult<Uint8Array>) => void;
    const reader = {
      read: vi.fn(() => new Promise<ReadableStreamReadResult<Uint8Array>>(resolve => { resolveRead = resolve; })),
      cancel: vi.fn().mockResolvedValue(undefined),
      releaseLock: vi.fn(),
    } as unknown as ReadableStreamDefaultReader<Uint8Array>;
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(csrfResponse("token"))
      .mockResolvedValueOnce(streamResponse(reader));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");
    const controller = new AbortController();

    const pending = api.streamMessage("conversation-1", "hello", () => undefined, "request-3", [], controller.signal);
    await flushMicrotasks();
    controller.abort();
    const failure = await pending.catch(error => error);

    expect(failure).toBeInstanceOf(DOMException);
    expect(failure.name).toBe("AbortError");
    expect(failure.code).not.toBe("STREAM_TIMEOUT");
    resolveRead({ done: true, value: undefined });
    await flushMicrotasks();
  });
});
