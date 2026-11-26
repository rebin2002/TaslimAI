import { describe, expect, it } from "vitest";
import {
  MUSIC_STUDIO_PROVIDER_ACCESS,
  canRequestMusicGeneration,
  decideMusicStudioSubmit,
  isMusicStudioGenerationPaused,
  musicStudioPreflightCopy,
} from "./musicStudioPreflight";
import { translations } from "./i18n";

// A complete, valid brief: everything the studio asks for and nothing that
// should block a submission once the studio is open.
const readyBrief = {
  access: "open" as const,
  workspaceReady: true,
  submitting: false,
  referenceSelected: false,
  description: "A calm cinematic bed for a two minute explainer",
  purpose: "Background music for a product walkthrough",
};

describe("Music Studio provider-unavailable preflight", () => {
  it("publishes the studio as paused while no approved music provider is configured", () => {
    // The single published posture. apps/api/appsettings.json keeps
    // MusicGeneration:Enabled false, so the browser must not offer a request
    // that the API refuses with 503 MUSIC_STUDIO_UNAVAILABLE before a job exists.
    expect(MUSIC_STUDIO_PROVIDER_ACCESS).toBe("paused");
    expect(isMusicStudioGenerationPaused()).toBe(true);
    expect(canRequestMusicGeneration()).toBe(false);
  });

  it("refuses a complete brief before any request is permitted while paused", () => {
    const decision = decideMusicStudioSubmit({ ...readyBrief, access: "paused" });
    expect(decision.permitted).toBe(false);
    expect(decision.reasonKey).toBe("music.providerUnavailableText");
    // The refusal reads as the preflight copy, not as a generic start failure.
    expect(decision.messageKey).toBe("music.providerUnavailableText");
    expect(decision.messageKey).not.toBe("music.createError");
  });

  it("still refuses an invalid brief and keeps the existing reasons", () => {
    const paused = decideMusicStudioSubmit({
      ...readyBrief,
      access: "paused",
      description: "ab",
      purpose: "cd",
      referenceSelected: true,
      workspaceReady: false,
    });
    // Paused outranks every other reason: nothing may leave the browser.
    expect(paused.messageKey).toBe("music.providerUnavailableText");

    expect(decideMusicStudioSubmit({ ...readyBrief, description: " ab " }).messageKey).toBe("music.required");
    expect(decideMusicStudioSubmit({ ...readyBrief, purpose: " ab " }).messageKey).toBe("music.required");
    expect(decideMusicStudioSubmit({ ...readyBrief, workspaceReady: false }).messageKey).toBe("music.required");
    // A three character brief trims to exactly the server's lower bound.
    expect(decideMusicStudioSubmit({ ...readyBrief, description: "abc", purpose: "abc" }).permitted).toBe(true);
    expect(decideMusicStudioSubmit({ ...readyBrief, description: "ab ", purpose: "abc" }).permitted).toBe(false);
    expect(decideMusicStudioSubmit({ ...readyBrief, referenceSelected: true }).messageKey).toBe("music.reference.note");
    // An in-flight submission must not overwrite the visible message.
    const submitting = decideMusicStudioSubmit({ ...readyBrief, submitting: true });
    expect(submitting.permitted).toBe(false);
    expect(submitting.messageKey).toBeNull();
  });

  it("permits the brief once the studio is open, with no preflight reason", () => {
    expect(decideMusicStudioSubmit(readyBrief)).toEqual({ permitted: true, messageKey: null, reasonKey: null });
    expect(musicStudioPreflightCopy("open")).toBeNull();
  });

  it("names the same copy for the notice and the reason on the disabled action", () => {
    const copy = musicStudioPreflightCopy("paused");
    expect(copy).not.toBeNull();
    const decision = decideMusicStudioSubmit({ ...readyBrief, access: "paused" });
    // One source of truth: the disabled control points at the notice text.
    expect(decision.reasonKey).toBe(copy!.textKey);
  });

  it("resolves every preflight key to translated copy in each launch locale", () => {
    const copy = musicStudioPreflightCopy("paused")!;
    const keys = [copy.eyebrowKey, copy.titleKey, copy.textKey, "music.required", "music.reference.note"];
    for (const locale of ["en", "ar", "ku"] as const) {
      for (const key of keys) {
        const value = translations[locale][key];
        expect(value, `${locale}:${key}`).toBeTruthy();
        expect(value.trim(), `${locale}:${key}`).toBe(value);
      }
    }
    // The Arabic and Sorani refusal must be translated copy rather than an
    // English fallback, since it is the only explanation a paused user gets.
    for (const locale of ["ar", "ku"] as const) {
      expect(translations[locale][copy.textKey]).not.toBe(translations.en[copy.textKey]);
    }
  });
});
