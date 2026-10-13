import { describe, expect, it } from "vitest";
import { createSseParser, SSE_MAX_EVENT_CHARS, SseProtocolError, type TaslimSseEvent } from "./sse";

function parse(chunks: string[]) {
  const events: TaslimSseEvent[] = [];
  const parser = createSseParser(event => events.push(event));
  for (const chunk of chunks) parser.push(chunk);
  parser.end();
  return events;
}

const completed = (lineEnding = "\n") => `event: message.completed${lineEnding}data: {"ok":true}${lineEnding}${lineEnding}`;

describe("Taslim SSE parser", () => {
  it("parses LF-separated events", () => {
    expect(parse([completed()])[0].type).toBe("message.completed");
  });

  it("parses CRLF-separated events", () => {
    expect(parse([completed("\r\n")])[0].data).toEqual({ ok: true });
  });

  it("parses an event split across arbitrary fetch chunks", () => {
    const source = completed();
    expect(parse([source.slice(0, 4), source.slice(4, 19), source.slice(19)])[0].type).toBe("message.completed");
  });

  it("parses multiple events in one chunk", () => {
    const events = parse(["event: message.started\ndata: {\"step\":1}\n\n" + completed()]);
    expect(events.map(event => event.type)).toEqual(["message.started", "message.completed"]);
  });

  it("joins multiline data fields before JSON parsing", () => {
    const events = parse(["event: message.completed\ndata: {\"text\":\ndata: 123}\n\n"]);
    expect(events[0].data).toEqual({ text: 123 });
  });

  it("accepts a completed terminal event", () => {
    expect(parse([completed()])).toHaveLength(1);
  });

  it("accepts a failed terminal event", () => {
    expect(parse(["event: message.failed\ndata: {\"code\":\"AI_GENERATION_FAILED\"}\n\n"])[0].type).toBe("message.failed");
  });

  it("dispatches a final buffered terminal event at EOF", () => {
    const source = completed();
    expect(parse([source.slice(0, -2)])[0].type).toBe("message.completed");
  });

  it("rejects EOF before a terminal event", () => {
    expect(() => parse(["event: message.started\ndata: {}\n\n"])).toThrow(SseProtocolError);
  });

  it("rejects malformed JSON instead of silently leaving generation active", () => {
    expect(() => parse(["event: message.completed\ndata: {bad}\n\n"])).toThrow(/Malformed JSON/);
  });

  it("rejects an oversized unterminated event before EOF", () => {
    const parser = createSseParser(() => undefined);
    expect(() => parser.push(`event: message.completed\ndata: ${"x".repeat(SSE_MAX_EVENT_CHARS)}`)).toThrow(/maximum size/);
  });

  it("rejects cumulative multiline data before JSON parsing", () => {
    const parser = createSseParser(() => undefined);
    parser.push("event: message.completed\n");
    expect(() => parser.push(`data: ${"x".repeat(SSE_MAX_EVENT_CHARS / 2)}\ndata: ${"y".repeat(SSE_MAX_EVENT_CHARS / 2)}\n\n`)).toThrow(/maximum size/);
  });

  it("accepts a large event below the maximum size", () => {
    const payload = JSON.stringify({ value: "x".repeat(SSE_MAX_EVENT_CHARS - 128) });
    expect(() => parse([`event: message.completed\ndata: ${payload}\n\n`])).not.toThrow();
  });

  it("rejects a second terminal or post-terminal event", () => {
    expect(() => parse([completed() + "event: message.failed\ndata: {\"code\":\"late\"}\n\n"])).toThrow(/after its terminal event/);
  });

  it("ignores keepalive comments and mixed line endings", () => {
    expect(parse([": keepalive\r\n\r\nevent: message.completed\ndata: {\"ok\":true}\n\n"])).toHaveLength(1);
  });
});
