export type TaslimSseEvent<TData extends Record<string, unknown> = Record<string, unknown>> = {
  type: "message.started" | "message.delta" | "message.completed" | "message.failed";
  data: TData;
};

const terminalEvents = new Set(["message.completed", "message.failed"]);
const supportedEvents = new Set(["message.started", "message.delta", "message.completed", "message.failed"]);

export class SseProtocolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SseProtocolError";
  }
}

export type SseParser = {
  push(chunk: string): void;
  end(): void;
  hasTerminalEvent(): boolean;
};

export function createSseParser<TData extends Record<string, unknown> = Record<string, unknown>>(onEvent: (event: TaslimSseEvent<TData>) => void): SseParser {
  let buffer = "";
  let eventName = "";
  let dataLines: string[] = [];
  let terminal = false;

  function resetEvent() {
    eventName = "";
    dataLines = [];
  }

  function dispatchEvent() {
    if (!eventName && dataLines.length === 0) return;
    if (!eventName) throw new SseProtocolError("SSE event is missing its event name.");
    if (dataLines.length === 0) throw new SseProtocolError(`SSE event ${eventName} is missing data.`);
    if (!supportedEvents.has(eventName)) throw new SseProtocolError(`Unsupported Taslim SSE event: ${eventName}`);
    if (terminal) throw new SseProtocolError("Taslim SSE stream emitted an event after its terminal event.");

    let parsed: unknown;
    try {
      parsed = JSON.parse(dataLines.join("\n"));
    } catch {
      throw new SseProtocolError(`Malformed JSON in Taslim SSE event: ${eventName}`);
    }
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      throw new SseProtocolError(`Invalid data payload in Taslim SSE event: ${eventName}`);
    }

    const event = { type: eventName as TaslimSseEvent<TData>["type"], data: parsed as TData };
    onEvent(event);
    if (terminalEvents.has(event.type)) terminal = true;
    resetEvent();
  }

  function processLine(line: string) {
    if (line.length === 0) {
      dispatchEvent();
      return;
    }
    if (line.startsWith(":")) return;

    const separator = line.indexOf(":");
    const field = separator >= 0 ? line.slice(0, separator) : line;
    const value = separator >= 0 ? line.slice(separator + 1).replace(/^ /, "") : "";
    if (field === "event") eventName = value;
    else if (field === "data") dataLines.push(value);
    else if (field === "id" || field === "retry") return;
    // Unknown SSE fields are ignored according to the SSE parsing model.
  }

  function push(chunk: string) {
    buffer += chunk;
    while (true) {
      const match = /(\r\n|\n|\r)/.exec(buffer);
      if (!match || (match[0] === "\r" && match.index + 1 === buffer.length)) return;
      const line = buffer.slice(0, match.index);
      buffer = buffer.slice(match.index + match[0].length);
      processLine(line);
    }
  }

  function end() {
    if (buffer.length > 0) {
      processLine(buffer.endsWith("\r") ? buffer.slice(0, -1) : buffer);
      buffer = "";
    }
    if (eventName || dataLines.length > 0) dispatchEvent();
    if (!terminal) throw new SseProtocolError("Taslim SSE stream ended before a terminal event.");
  }

  return { push, end, hasTerminalEvent: () => terminal };
}
