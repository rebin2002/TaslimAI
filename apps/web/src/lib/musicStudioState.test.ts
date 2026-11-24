import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import { canCancelMusicJob, isMusicJob, isMusicOutcomeUnconfirmed, isMusicProviderUnavailable, musicStudioFailureCopy, parseMusicJobResult, safeMusicJobView } from "./musicStudioState";
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

  it("gives every music failure its own copy and only the unconfirmed one loses the retry action", () => {
    const copy = (code: string | null, status: GenerationJob["status"] = "Failed") => musicStudioFailureCopy(job({ status, errorCode: code }), (key) => key);
    expect(copy("MUSIC_PROVIDER_SUBMISSION_UNCONFIRMED")).toEqual({ title: "music.unconfirmedTitle", text: "music.unconfirmedText", allowRetry: false });
    expect(copy("MUSIC_SUBMISSION_RETRY_BLOCKED").allowRetry).toBe(false);
    expect(copy("MUSIC_PROVIDER_UNAVAILABLE")).toEqual({ title: "music.providerUnavailableTitle", text: "music.providerUnavailableText", allowRetry: true });
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
