import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const routeError = readFileSync(new URL("./error.tsx", import.meta.url), "utf8");

describe("Image Studio route recovery", () => {
  it("provides an Image-scoped accessible retry boundary", () => {
    expect(routeError).toContain('export default function ImageStudioError');
    expect(routeError).toContain('t("image.title")');
    expect(routeError).toContain('role="alert"');
    expect(routeError).toContain('aria-live="assertive"');
    expect(routeError).toContain('type="button"');
    expect(routeError).toContain('onClick={reset}');
    expect(routeError).toContain('t("error.retry")');
    expect(routeError).toContain('href="/assets"');
  });

  it("does not expose route error details", () => {
    expect(routeError).not.toContain("error.message");
    expect(routeError).not.toContain("error.stack");
    expect(routeError).not.toContain("error.cause");
    expect(routeError).toContain('console.error("Image Studio route recovery", "route_error")');
  });
});
