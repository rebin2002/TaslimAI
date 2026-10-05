import { describe, expect, it } from "vitest";
import { PROTECTED_PAGE_AUTH_TIMEOUT_MS, protectedPageLoginPath } from "./ProtectedPage";

describe("protected page loading boundary", () => {
  it("uses a finite session bootstrap timeout", () => {
    expect(PROTECTED_PAGE_AUTH_TIMEOUT_MS).toBe(10_000);
    expect(PROTECTED_PAGE_AUTH_TIMEOUT_MS).toBeGreaterThan(0);
  });

  it("keeps login return paths local and rejects control characters", () => {
    expect(protectedPageLoginPath("/personal/health")).toBe("/login?next=%2Fpersonal%2Fhealth");
    expect(protectedPageLoginPath("//evil.example/health")).toBe("/login?next=%2F");
    expect(protectedPageLoginPath(`/personal/health${String.fromCharCode(0)}`)).toBe("/login?next=%2F");
  });
});
