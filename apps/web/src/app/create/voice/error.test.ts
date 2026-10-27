import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const routeError = readFileSync(new URL("./error.tsx", import.meta.url), "utf8");

describe("Voice Studio route recovery", () => {
  it("provides a Voice-scoped accessible retry boundary", () => {
    expect(routeError).toContain("export default function VoiceStudioError");
    expect(routeError).toContain('t("voice.title")');
    expect(routeError).toContain('role="alert"');
    expect(routeError).toContain('aria-live="assertive"');
    expect(routeError).toContain('type="button"');
    expect(routeError).toContain("onClick={reset}");
    expect(routeError).toContain('t("error.retry")');
    expect(routeError).toContain('href="/assets"');
  });

  it("does not expose route error details", () => {
    expect(routeError).not.toContain("error.message");
    expect(routeError).not.toContain("error.stack");
    expect(routeError).not.toContain("error.cause");
    expect(routeError).toContain('console.error("Voice Studio route recovery", "route_error")');
  });
});
