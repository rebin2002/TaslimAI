import { describe, expect, it } from "vitest";
import { authSuccessPath } from "./authRedirect";

describe("auth success redirect", () => {
  it("preserves a local protected-page path", () => {
    expect(authSuccessPath("/personal/health")).toBe("/personal/health");
    expect(authSuccessPath("/projects/project-1?tab=overview")).toBe("/projects/project-1?tab=overview");
  });

  it("rejects external and malformed destinations", () => {
    expect(authSuccessPath(null)).toBeNull();
    expect(authSuccessPath("https://evil.example/collect")).toBeNull();
    expect(authSuccessPath("//evil.example/collect")).toBeNull();
    expect(authSuccessPath("/\\\\evil.example/collect")).toBeNull();
    expect(authSuccessPath(`/personal/health${String.fromCharCode(0)}`)).toBeNull();
  });
});
