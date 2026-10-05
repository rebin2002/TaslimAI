export const CHAT_STREAM_WATCHDOG_TIMEOUT_MS = 45_000;

export type ChatStreamTransportErrorCode = "STREAM_TIMEOUT" | "STREAM_CONNECTION_LOST";

export class ChatStreamTransportError extends Error {
  constructor(public readonly code: ChatStreamTransportErrorCode, message: string) {
    super(message);
    this.name = "ChatStreamTransportError";
  }
}

function abortReason(signal?: AbortSignal) {
  return signal?.reason ?? new DOMException("The operation was aborted.", "AbortError");
}

/**
 * Bounds one fetch/read operation without converting a caller-owned abort into
 * a transport failure. Results that settle after the watchdog have no effect.
 */
export function awaitWithChatStreamWatchdog<T>(
  operation: PromiseLike<T>,
  signal?: AbortSignal,
  timeoutMs = CHAT_STREAM_WATCHDOG_TIMEOUT_MS,
  onTimeout?: () => void,
): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    let settled = false;

    const cleanup = () => {
      clearTimeout(timeout);
      signal?.removeEventListener("abort", onAbort);
    };

    const settleResolve = (value: T) => {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(value);
    };

    const settleReject = (error: unknown) => {
      if (settled) return;
      settled = true;
      cleanup();
      reject(error);
    };

    const onAbort = () => settleReject(abortReason(signal));

    const timeout = setTimeout(() => {
      if (settled) return;
      settled = true;
      cleanup();
      onTimeout?.();
      reject(new ChatStreamTransportError("STREAM_TIMEOUT", "The chat stream timed out."));
    }, timeoutMs);

    if (signal?.aborted) onAbort();
    else signal?.addEventListener("abort", onAbort, { once: true });

    operation.then(
      value => settleResolve(value),
      () => {
        if (signal?.aborted) settleReject(abortReason(signal));
        else settleReject(new ChatStreamTransportError("STREAM_CONNECTION_LOST", "The chat stream connection was lost."));
      },
    );
  });
}
