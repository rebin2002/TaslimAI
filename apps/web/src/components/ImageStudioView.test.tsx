import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "../lib/api";
import { localizedImageErrorMessage } from "../lib/imageStudioErrors";
import { translate, type Locale } from "../lib/i18n";
import { ImageFailureCanvas, ImageStudioView } from "./ImageStudioView";

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: { id: "workspace-1" } }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: (key: string) => key }),
}));

const studioSource = readFileSync(new URL("./ImageStudioView.tsx", import.meta.url), "utf8");

/** Builds the same labels the studio renders for a terminal failure. */
function failureLabels(message: string, locale: Locale = "en") {
  return {
    eyebrow: translate(locale, "image.failedEyebrow"),
    title: translate(locale, "image.failedTitle"),
    message,
    retry: translate(locale, "image.retry"),
    retrying: translate(locale, "image.working"),
  };
}

function canvasHtml(labels: ReturnType<typeof failureLabels>, working: boolean) {
  return renderToStaticMarkup(<ImageFailureCanvas labels={labels} working={working} onRetry={() => {}} />);
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("Image Studio upload failure retry accessibility", () => {
  it("describes the mocked upload failure to the retry action without leaking server text", async () => {
    // Mock the storage upload that persists the generated image: it fails with the stable
    // Taslim code and a raw server message that must never reach the screen reader output.
    vi.spyOn(api, "uploadFile").mockRejectedValue(
      new ApiError(503, "PUT https://r2.example/objects/9f2c failed: x-amz-checksum mismatch", undefined, "IMAGE_OUTPUT_STORAGE_FAILED"),
    );
    const failure = await api.uploadFile("workspace-1", new File([new Uint8Array([1])], "reference.png", { type: "image/png" }))
      .then(() => null)
      .catch((caught: unknown) => caught as ApiError);

    expect(failure?.code).toBe("IMAGE_OUTPUT_STORAGE_FAILED");
    const reason = localizedImageErrorMessage(failure?.code, (key) => translate("en", key), "image.failedText");
    expect(reason).toBe(translate("en", "image.error.storageFailed"));

    const html = canvasHtml(failureLabels(reason), false);
    const describedMessageId = /<p id="([^"]+)">/.exec(html)?.[1];
    expect(describedMessageId, "the failure reason must be a referenced element").toBeTruthy();
    expect(html).toContain(`aria-describedby="${describedMessageId}"`);
    expect(html).toContain("could not be saved");
    expect(html).not.toContain("r2.example");
    expect(html).not.toContain("x-amz-checksum");
  });

  it("keeps the retry control focusable and exposed instead of dropping browser focus", () => {
    const html = canvasHtml(failureLabels(translate("en", "image.error.storageFailed")), true);
    expect(html).toMatch(/<button[^>]*class="image-retry-button"[^>]*>/);
    // A `disabled` button removes itself from the a11y tree and the focused element loses focus.
    expect(html).not.toMatch(/<button[^>]*\sdisabled(?:=|\s|>)/);
    expect(html).toContain('aria-disabled="true"');
    expect(html).toContain('aria-busy="true"');
    // The retry-in-progress state is part of the focused control's accessible name.
    expect(html).toContain(translate("en", "image.working"));
    expect(html).toContain('<div class="image-failure-canvas" aria-busy="true"');
  });

  it("restores the idle retry semantics once the upload attempt finishes", () => {
    const html = canvasHtml(failureLabels(translate("en", "image.error.storageFailed")), false);
    expect(html).toContain('aria-busy="false"');
    expect(html).not.toContain("aria-disabled");
    expect(html).toContain(translate("en", "image.retry"));
  });

  it("localizes the upload-failure retry path in Arabic and Kurdish Sorani", () => {
    for (const locale of ["ar", "ku"] as const) {
      const reason = localizedImageErrorMessage("IMAGE_OUTPUT_STORAGE_FAILED", (key) => translate(locale, key), "image.failedText");
      expect(reason, locale).toBe(translate(locale, "image.error.storageFailed"));
      expect(reason).not.toBe("image.error.storageFailed");
      const html = canvasHtml(failureLabels(reason, locale), false);
      expect(html).toContain(translate(locale, "image.retry"));
      expect(html).toContain(`aria-describedby="image-failure-message"`);
    }
  });

  it("wires the accessible canvas into the studio and blocks re-entrant retry submissions", () => {
    expect(studioSource).toContain("<ImageFailureCanvas labels=");
    expect(studioSource).not.toMatch(/className="image-retry-button"[^>]*disabled=\{working\}/);
    expect(studioSource).toContain("if (working) return;");
    const html = renderToStaticMarkup(<ImageStudioView />);
    expect(html).toContain("image-result-canvas");
    expect(html).toContain('aria-live="polite"');
  });
});
