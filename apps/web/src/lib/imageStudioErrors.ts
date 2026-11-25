export type ImageStudioTranslator = (key: string) => string;

const imageErrorKeys: Record<string, string> = {
  IMAGE_STUDIO_UNAVAILABLE: "image.error.unavailable",
  IMAGE_PROVIDER_UNAVAILABLE: "image.error.providerUnavailable",
  IMAGE_PROVIDER_CONFIGURATION: "image.error.providerUnavailable",
  IMAGE_PROVIDER_RATE_LIMITED: "image.error.providerUnavailable",
  IMAGE_PROVIDER_TIMEOUT: "image.error.providerUnavailable",
  IMAGE_SAFETY_REFUSAL: "image.error.safety",
  IMAGE_REQUEST_INVALID: "image.error.requestInvalid",
  IMAGE_REFERENCE_NOT_SUPPORTED: "image.error.requestInvalid",
  IMAGE_STYLE_UNSUPPORTED: "image.error.requestInvalid",
  IMAGE_ASPECT_RATIO_UNSUPPORTED: "image.error.requestInvalid",
  IMAGE_QUALITY_UNSUPPORTED: "image.error.requestInvalid",
  PROJECT_NOT_IN_WORKSPACE: "image.error.requestInvalid",
  IMAGE_OUTPUT_INVALID: "image.error.outputInvalid",
  IMAGE_OUTPUT_STORAGE_FAILED: "image.error.storageFailed",
  IMAGE_CANCELLED: "image.error.cancelled",
  JOB_CANCELLED: "image.error.cancelled",
  IMAGE_GENERATION_FAILED: "image.error.failed",
  JOB_EXECUTION_FAILED: "image.error.failed",
  JOB_POISONED: "image.error.recoveryFailed",
  GENERATION_NO_BILLABLE_ASSET: "image.error.outputInvalid",
};

export function imageErrorTranslationKey(code: string | null | undefined): string | null {
  const normalizedCode = code?.trim().toUpperCase();
  if (!normalizedCode) return null;
  return imageErrorKeys[normalizedCode] ?? (normalizedCode.startsWith("IMAGE_") ? "image.error.failed" : null);
}

export function localizedImageErrorMessage(
  code: string | null | undefined,
  translate: ImageStudioTranslator,
  fallbackKey: string,
): string {
  return translate(imageErrorTranslationKey(code) ?? fallbackKey);
}
