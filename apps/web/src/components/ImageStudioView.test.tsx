import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "../lib/api";
import { localizedImageErrorMessage } from "../lib/imageStudioErrors";
import { translate, type Locale } from "../lib/i18n";
import { ImageFailureCanvas, ImageGeneratingCanvas, ImageStudioView, type ImageGeneratingCanvasLabels } from "./ImageStudioView";

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

/** Builds the same labels the studio renders while a generation is in progress. */
function generatingLabels(locale: Locale = "en"): ImageGeneratingCanvasLabels {
  return {
    eyebrow: translate(locale, "image.progressEyebrow"),
    title: translate(locale, "jobs.statusRunning"),
    message: translate(locale, "image.progressText"),
    progress: translate(locale, "jobs.progress"),
    cancel: translate(locale, "image.cancel"),
  };
}

describe("Image Studio cancellation busy state", () => {
  it("keeps the cancel control focusable while the cancellation request is in flight", () => {
    const html = renderToStaticMarkup(
      <ImageGeneratingCanvas labels={generatingLabels()} percent={40} canCancel working onCancel={() => {}} />,
    );
    expect(html).toMatch(/<button[^>]*class="image-cancel-button"[^>]*>/);
    // A `disabled` button removes itself from the a11y tree and the focused element loses focus.
    expect(html).not.toMatch(/<button[^>]*\sdisabled(?:=|\s|>)/);
    expect(html).toContain('aria-disabled="true"');
    expect(html).toContain('aria-busy="true"');
    // The control keeps its name: a cancellation must never be announced as a new start.
    expect(html).toContain(translate("en", "image.cancel"));
    expect(html).not.toContain(translate("en", "image.working"));
  });

  it("restores the idle cancel semantics once the request settles", () => {
    const html = renderToStaticMarkup(
      <ImageGeneratingCanvas labels={generatingLabels()} percent={40} canCancel working={false} onCancel={() => {}} />,
    );
    expect(html).toContain('aria-busy="false"');
    expect(html).not.toContain("aria-disabled");
    expect(html).toContain(translate("en", "image.cancel"));
  });

  it("removes the cancel affordance for a job that can no longer be cancelled", () => {
    const html = renderToStaticMarkup(
      <ImageGeneratingCanvas labels={generatingLabels()} percent={90} canCancel={false} working={false} onCancel={() => {}} />,
    );
    expect(html).not.toContain("image-cancel-button");
    expect(html).toContain("image-progress-track");
  });

  it("localizes the cancelling state in Arabic and Kurdish Sorani", () => {
    for (const locale of ["ar", "ku"] as const) {
      const html = renderToStaticMarkup(
        <ImageGeneratingCanvas labels={generatingLabels(locale)} percent={40} canCancel working onCancel={() => {}} />,
      );
      expect(html, locale).toContain(translate(locale, "image.cancel"));
      expect(html, locale).toContain(translate(locale, "image.progressEyebrow"));
    }
  });
});

describe("Image Studio active job state reconciliation", () => {
  it("merges every job read instead of overwriting with the latest response", () => {
    expect(studioSource).toContain("mergeImageJobUpdate");
    // An unconditional write lets a late poll undo a cancellation or drop a settled result.
    expect(studioSource).not.toMatch(/setCurrent\(next\)/);
    expect(studioSource).not.toMatch(/setCurrent\(job\)/);
    expect(studioSource).toMatch(/setCurrent\(\(previous\) => mergeImageJobUpdate\(previous, (next|job)\)\)/);
  });

  it("keeps polling after a frame that changed nothing", () => {
    // A rejected stale frame leaves the stored job identical, so a loop rescheduled only by that
    // state change would stop polling a generation that is still running.
    expect(studioSource).toContain("setPollTick((tick) => tick + 1);");
    expect(studioSource).toMatch(/\[current, pollRetry, pollTick, t, user\?\.id, workspace\?\.id\]/);
  });

  it("retries a failed restore within a bounded budget and says why when it gives up", () => {
    const restoreBody = /const readStoredJob = \(attempt: number\) => \{([\s\S]*?)\n    \};/.exec(studioSource)?.[1] ?? "";
    expect(restoreBody, "the restore must read through a bounded retry budget").toContain("shouldRetryImageRestore(status, attempt)");
    expect(restoreBody).toContain("readStoredJob(attempt + 1)");
    expect(restoreBody).toContain("nextImageRestoreDelay(attempt)");
    // An exhausted budget explains the missing progress through the panel's alert region.
    expect(restoreBody).toMatch(/if \(!shouldRetryImageRestore\(status, attempt\)\) \{\s*\n\s*setError\(localizedImageErrorMessage\(code, t, "image\.pollError"\)\);/);
    // A transport failure keeps the pointer: the generation it names may still finish server-side,
    // and only an inaccessible job is allowed to drop it.
    const orphanBranch = /if \(shouldClearImageJobAfterPollError\(status\)\) \{([\s\S]*?)\n          return;/.exec(restoreBody)?.[1] ?? "";
    expect(orphanBranch).toContain("clearImageActiveJobId(user.id, workspace.id)");
    expect(restoreBody.slice(restoreBody.indexOf("shouldRetryImageRestore")).indexOf("clearImageActiveJobId")).toBe(-1);
  });

  it("blocks a duplicate cancellation request for a job that cannot be cancelled again", () => {
    const cancelBody = /async function cancel\(\) \{([\s\S]*?)\n  \}/.exec(studioSource)?.[1] ?? "";
    expect(cancelBody).toContain("if (working) return;");
    expect(cancelBody).toContain("canCancelImageJob(job)");
  });
});
