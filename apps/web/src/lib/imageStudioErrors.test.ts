import { describe, expect, it } from "vitest";
import { imageErrorTranslationKey, localizedImageErrorMessage } from "./imageStudioErrors";

describe("Image Studio error localization", () => {
  const translate = (key: string) => `translated:${key}`;

  it("maps stable provider and lifecycle codes to localized catalog keys", () => {
    expect(imageErrorTranslationKey("IMAGE_PROVIDER_UNAVAILABLE")).toBe("image.error.providerUnavailable");
    expect(imageErrorTranslationKey("IMAGE_SAFETY_REFUSAL")).toBe("image.error.safety");
    expect(imageErrorTranslationKey("IMAGE_CANCELLED")).toBe("image.error.cancelled");
    expect(imageErrorTranslationKey(" job_cancelled ")).toBe("image.error.cancelled");
    expect(imageErrorTranslationKey("JOB_POISONED")).toBe("image.error.recoveryFailed");
  });

  it("uses a generic localized image message for future IMAGE codes", () => {
    expect(localizedImageErrorMessage("IMAGE_FUTURE_CODE", translate, "image.createError")).toBe("translated:image.error.failed");
  });

  it("uses operation-specific localized fallbacks for transport and unknown errors", () => {
    expect(localizedImageErrorMessage(null, translate, "image.pollError")).toBe("translated:image.pollError");
    expect(localizedImageErrorMessage("UNRELATED_ERROR", translate, "image.createError")).toBe("translated:image.createError");
  });

  it("does not accept or expose server error text", () => {
    expect(localizedImageErrorMessage("IMAGE_OUTPUT_STORAGE_FAILED", translate, "image.failedText")).not.toContain("provider internals");
    expect(localizedImageErrorMessage("IMAGE_OUTPUT_STORAGE_FAILED", translate, "image.failedText")).toBe("translated:image.error.storageFailed");
  });
});
