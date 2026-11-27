import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import { canCancelMusicJob, isMusicCancellationUnresolved, isMusicJob, isMusicOutcomeUnconfirmed, isMusicProviderUnavailable, musicStudioFailureCopy, parseMusicJobResult, safeMusicJobView } from "./musicStudioState";
import { locales, translations } from "./i18n";

function job(overrides: Partial<GenerationJob> = {}): GenerationJob {
  return {
    id: "music-job-1", workspaceId: "workspace-1", projectId: null, jobType: "music.generate", status: "Running", title: "Calm bed", progressPercent: 42,
    resultJson: null, errorCode: null, errorMessage: null, cancellationRequested: false, createdAt: "2026-01-01T00:00:00Z", queuedAt: "2026-01-01T00:00:00Z",
    startedAt: "2026-01-01T00:00:01Z", completedAt: null, failedAt: null, cancelledAt: null, outputs: [], ...overrides,
  };
}

describe("music studio state", () => {
  it("recognizes music jobs and allows cancellation before terminal states", () => {
    expect(isMusicJob(job())).toBe(true);
    expect(canCancelMusicJob(job({ status: "Queued" }))).toBe(true);
    expect(canCancelMusicJob(job({ status: "Succeeded" }))).toBe(false);
    expect(canCancelMusicJob(job({ cancellationRequested: true }))).toBe(false);
  });

  it("parses only safe Asset result fields", () => {
    const result = parseMusicJobResult(job({ status: "Succeeded", progressPercent: 100, resultJson: '{"assetId":"asset-1","assetType":"music","format":"mp3","durationSeconds":60,"provider":"secret","model":"secret"}' }));
    expect(result).toMatchObject({ assetId: "asset-1", assetType: "music", format: "mp3", durationSeconds: 60 });
    expect(result).not.toHaveProperty("provider");
    expect(result).not.toHaveProperty("model");
  });

  it("recognizes the unconfirmed outcome and hides the retry that would double-bill", () => {
    const unconfirmed = job({ status: "Failed", errorCode: "MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED" });
    expect(isMusicOutcomeUnconfirmed(unconfirmed)).toBe(true);
    expect(isMusicOutcomeUnconfirmed(job({ status: "Failed", errorCode: "MUSIC_SUBMISSION_RETRY_BLOCKED" }))).toBe(true);
    expect(isMusicOutcomeUnconfirmed(job({ status: "Running", errorCode: "MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED" }))).toBe(false);
    expect(isMusicOutcomeUnconfirmed(job({ status: "Failed", errorCode: "MUSIC_GENERATION_FAILED" }))).toBe(false);
    expect(isMusicOutcomeUnconfirmed(job({ status: "Succeeded" }))).toBe(false);
  });

  it("keeps ordinary failures retryable and separates known delivery failures from uncertain outcomes", () => {
    const copy = (code: string | null, status: GenerationJob["status"] = "Failed") => musicStudioFailureCopy(job({ status, errorCode: code }), (key) => key);
    expect(copy("MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED")).toEqual({ title: "music.unconfirmedTitle", text: "music.unconfirmedText", allowRetry: false });
    expect(copy("MUSIC_SUBMISSION_RETRY_BLOCKED")).toEqual({ title: "music.unconfirmedTitle", text: "music.unconfirmedText", allowRetry: false });
    expect(copy("MUSIC_SUBMISSION_RETRY_BLOCKED", "Cancelled").allowRetry).toBe(false);
    for (const code of ["MUSIC_OUTPUT_RETRIEVAL_FAILED", "MUSIC_RETRIEVAL_RETRY_BLOCKED"]) {
      expect(copy(code)).toEqual({ title: "music.deliveryFailedTitle", text: "music.deliveryFailedText", allowRetry: false });
      for (const locale of locales) expect(translations[locale]["music.deliveryFailedText"]).toBeTruthy();
    }
    expect(copy("MUSIC_PROVIDER_UNAVAILABLE")).toEqual({ title: "music.providerUnavailableTitle", text: "music.failedServiceUnavailableText", allowRetry: true });
    expect(copy("MUSIC_GENERATION_FAILED")).toEqual({ title: "music.failureTitle", text: "music.failureText", allowRetry: true });
    expect(copy(null, "Cancelled")).toEqual({ title: "music.cancelledTitle", text: "music.cancelledText", allowRetry: true });
    // The unconfirmed text is the one that must not assert an outcome either way.
    for (const locale of locales) {
      const text = translations[locale]["music.unconfirmedText"];
      expect(typeof text).toBe("string");
      expect(text.length).toBeGreaterThan(0);
      expect(text).not.toMatch(/was generated|has been created/i);
      for (const other of ["music.failureText", "music.cancelledText"]) {
        expect(text).not.toEqual(translations[locale][other]);
      }
    }
  });
  it("refuses the retry affordance for a cancellation of an accepted submission, and keeps it for a plain one", () => {
    // F-36: the panel is the only place a member can start a second paid generation after cancelling,
    // so the affordance must follow the server's refusal instead of the job status.
    expect(musicStudioFailureCopy(job({ status: "Cancelled", errorCode: "MUSIC_CANCELLED_UNCONFIRMED_SUBMISSION" }), (key) => key))
      .toEqual({ title: "music.cancelledUnconfirmedTitle", text: "music.cancelledUnconfirmedText", allowRetry: false });
    expect(musicStudioFailureCopy(job({ status: "Cancelled", errorCode: "MUSIC_CANCELLED_UNDELIVERED_OUTPUT" }), (key) => key))
      .toEqual({ title: "music.cancelledUndeliveredTitle", text: "music.cancelledUndeliveredText", allowRetry: false });
    // A cancellation before the provider accepted anything is genuinely safe to redo and keeps its copy
    // and its action, as does a cancelled job whose code was never set.
    expect(musicStudioFailureCopy(job({ status: "Cancelled", errorCode: "MUSIC_CANCELLED" }), (key) => key))
      .toEqual({ title: "music.cancelledTitle", text: "music.cancelledText", allowRetry: true });
    expect(musicStudioFailureCopy(job({ status: "Cancelled", errorCode: null }), (key) => key).allowRetry).toBe(true);
    expect(isMusicCancellationUnresolved(job({ status: "Cancelled", errorCode: "MUSIC_CANCELLED" }))).toBe(false);
    expect(isMusicCancellationUnresolved(job({ status: "Cancelled", errorCode: "MUSIC_CANCELLED_UNCONFIRMED_SUBMISSION" }))).toBe(true);
    expect(isMusicCancellationUnresolved(job({ status: "Failed", errorCode: "MUSIC_CANCELLED_UNCONFIRMED_SUBMISSION" }))).toBe(false);
    // The failure family predicate is unchanged, so an accepted-but-unconfirmed FAILED job keeps its own
    // copy instead of being relabelled as a cancellation.
    expect(musicStudioFailureCopy(job({ status: "Failed", errorCode: "MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED" }), (key) => key))
      .toEqual({ title: "music.unconfirmedTitle", text: "music.unconfirmedText", allowRetry: false });
    for (const locale of locales) {
      const unconfirmed = translations[locale]["music.cancelledUnconfirmedText"];
      const undelivered = translations[locale]["music.cancelledUndeliveredText"];
      expect(typeof unconfirmed).toBe("string");
      expect(typeof undelivered).toBe("string");
      expect(unconfirmed.length).toBeGreaterThan(0);
      expect(undelivered.length).toBeGreaterThan(0);
      // Neither cancellation copy may invite another submission.
      expect(unconfirmed).not.toMatch(/try again|please retry/i);
      expect(undelivered).not.toMatch(/try again|please retry/i);
      // The unknown-outcome copy must not assert that audio exists, and must be distinct from every
      // neighbouring music message in the same locale.
      expect(unconfirmed).not.toMatch(/was generated|has been created/i);
      const neighbours = ["music.cancelledText", "music.unconfirmedText", "music.failureText", "music.cancelledUndeliveredText"];
      for (const other of neighbours) {
        expect(unconfirmed).not.toEqual(translations[locale][other]);
      }
      expect(undelivered).not.toEqual(translations[locale]["music.cancelledText"]);
    }
  });

  it("rejects a succeeded result whose asset identity is empty or whitespace-only", () => {
    for (const assetId of ["", "   ", "\t", "\n "]) {
      const malformed = job({ status: "Succeeded", resultJson: JSON.stringify({ assetId, assetType: "music" }) });
      expect(parseMusicJobResult(malformed)).toBeNull();
      expect(safeMusicJobView(malformed)?.result).toBeNull();
    }
  });

  it("exposes a safe failed view and ignores malformed results", () => {
    const failed = job({ status: "Failed", errorCode: "MUSIC_PROVIDER_UNAVAILABLE", errorMessage: "Music generation is temporarily unavailable. Please try again later." });
    expect(safeMusicJobView(failed)).toMatchObject({ status: "Failed", errorCode: "MUSIC_PROVIDER_UNAVAILABLE" });
    expect(isMusicProviderUnavailable(failed)).toBe(true);
    expect(isMusicProviderUnavailable(job({ status: "Failed", errorCode: "MUSIC_GENERATION_FAILED" }))).toBe(false);
    expect(parseMusicJobResult(job({ status: "Succeeded", resultJson: "not-json" }))).toBeNull();
  });
});
