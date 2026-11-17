// Music Studio preflight: the single browser-side source of truth for whether
// /create/music may hand a brief to the API at all, and for the copy that
// explains the answer. Pure and provider-free: importing this module performs no
// network, storage or rendering work, so the gate can be tested on its own.

export type MusicStudioAccess = "paused" | "open";

// The published posture of Music Studio. `apps/api/appsettings.json` keeps
// `MusicGeneration:Enabled` false and the server-only credentials empty, so
// `POST /api/music-generation/jobs` answers 503 MUSIC_STUDIO_UNAVAILABLE before
// a job exists, before cost preflight and before any provider could be
// contacted (docs/MUSIC_STUDIO_ARCHITECTURE.md, "Deferred work"). Until an
// approved provider account, server-only credentials, private storage
// configuration and the commercial gate exist, the browser must offer exactly
// what the API enforces. Changing this one value is the only way the studio
// opens; the notice, the hero status mark, the disabled primary action and the
// submit guard all read it, so copy and affordance cannot drift apart again.
export const MUSIC_STUDIO_PROVIDER_ACCESS: MusicStudioAccess = "paused";

// `MusicGenerationRequestValidator` and `MusicGenerationController` both measure
// the description and the purpose with `Trim().Length >= 3`.
const minimumBriefCharacters = 3;

export type MusicStudioBriefDraft = Readonly<{
  access?: MusicStudioAccess;
  workspaceReady: boolean;
  submitting: boolean;
  referenceSelected: boolean;
  description: string;
  purpose: string;
}>;

export type MusicStudioSubmitDecision = Readonly<{
  // Whether the brief may leave the browser at all.
  permitted: boolean;
  // Inline message to show when the guard is reached anyway (implicit form
  // submission), or null to leave the visible message untouched.
  messageKey: string | null;
  // Studio-level reason shown on the disabled action, or null when the action
  // is only waiting on the user's own brief.
  reasonKey: string | null;
}>;

export type MusicStudioPreflightCopy = Readonly<{
  eyebrowKey: string;
  titleKey: string;
  textKey: string;
}>;

export function musicStudioAccess(access: MusicStudioAccess = MUSIC_STUDIO_PROVIDER_ACCESS): MusicStudioAccess {
  return access;
}

export function isMusicStudioGenerationPaused(access: MusicStudioAccess = MUSIC_STUDIO_PROVIDER_ACCESS): boolean {
  return musicStudioAccess(access) !== "open";
}

export function canRequestMusicGeneration(access: MusicStudioAccess = MUSIC_STUDIO_PROVIDER_ACCESS): boolean {
  return musicStudioAccess(access) === "open";
}

// The preflight notice, or null when the studio is open and the copy would be a
// claim the browser cannot support.
export function musicStudioPreflightCopy(access: MusicStudioAccess = MUSIC_STUDIO_PROVIDER_ACCESS): MusicStudioPreflightCopy | null {
  if (!isMusicStudioGenerationPaused(access)) return null;
  return {
    eyebrowKey: "music.providerStatus",
    titleKey: "music.providerUnavailableTitle",
    textKey: "music.providerUnavailableText",
  };
}

function briefIsComplete(draft: MusicStudioBriefDraft): boolean {
  return (
    draft.description.trim().length >= minimumBriefCharacters
    && draft.purpose.trim().length >= minimumBriefCharacters
  );
}

export function decideMusicStudioSubmit(draft: MusicStudioBriefDraft): MusicStudioSubmitDecision {
  const { access = MUSIC_STUDIO_PROVIDER_ACCESS } = draft;
  // Creation being paused outranks everything else: no private brief is sent to
  // a route that can only refuse it, and no request can ever reach a provider.
  if (isMusicStudioGenerationPaused(access)) {
    return { permitted: false, messageKey: musicStudioPreflightCopy(access)!.textKey, reasonKey: musicStudioPreflightCopy(access)!.textKey };
  }
  if (draft.submitting) return { permitted: false, messageKey: null, reasonKey: null };
  if (!draft.workspaceReady || !briefIsComplete(draft)) {
    return { permitted: false, messageKey: "music.required", reasonKey: null };
  }
  // The reference audio path is selection and preview only: the job contract has
  // no reference field yet, so never let a selection imply that it will reach a
  // (possibly paid) provider call.
  if (draft.referenceSelected) return { permitted: false, messageKey: "music.reference.note", reasonKey: null };
  return { permitted: true, messageKey: null, reasonKey: null };
}
