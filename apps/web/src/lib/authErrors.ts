import { ApiError } from "./api";

export type AuthErrorTranslationKey =
  | "auth.invalidCredentials"
  | "auth.csrfExpired"
  | "auth.validationError"
  | "auth.serverUnavailable";

export function getAuthErrorTranslationKey(error: unknown): AuthErrorTranslationKey {
  if (error instanceof ApiError && error.code === "INVALID_CREDENTIALS") return "auth.invalidCredentials";
  if (error instanceof ApiError && error.code === "CSRF_VALIDATION_FAILED") return "auth.csrfExpired";
  if (error instanceof ApiError && error.code === "VALIDATION_ERROR") return "auth.validationError";
  if (error instanceof Error && error.message === "CSRF token unavailable") return "auth.csrfExpired";
  return "auth.serverUnavailable";
}
