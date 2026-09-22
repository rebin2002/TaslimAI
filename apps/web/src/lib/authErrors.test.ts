import { describe, expect, it } from "vitest";
import { ApiError } from "./api";
import { getAuthErrorTranslationKey } from "./authErrors";

describe("getAuthErrorTranslationKey", () => {
  it("classifies invalid credentials without exposing server text", () => {
    expect(getAuthErrorTranslationKey(new ApiError(401, "Invalid email or password.", undefined, "INVALID_CREDENTIALS"))).toBe("auth.invalidCredentials");
  });

  it("classifies CSRF expiry and token-fetch failures for retry UX", () => {
    expect(getAuthErrorTranslationKey(new ApiError(400, "Request validation failed.", undefined, "CSRF_VALIDATION_FAILED"))).toBe("auth.csrfExpired");
    expect(getAuthErrorTranslationKey(new Error("CSRF token unavailable"))).toBe("auth.csrfExpired");
  });

  it("classifies validation, network, and unknown errors safely", () => {
    expect(getAuthErrorTranslationKey(new ApiError(400, "Please enter your email and password.", undefined, "VALIDATION_ERROR"))).toBe("auth.validationError");
    expect(getAuthErrorTranslationKey(new TypeError("Failed to fetch"))).toBe("auth.serverUnavailable");
    expect(getAuthErrorTranslationKey({ message: "provider internals" })).toBe("auth.serverUnavailable");
  });
});
