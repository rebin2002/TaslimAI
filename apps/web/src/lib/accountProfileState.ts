import type { ProfileInput, User } from "./api";
import type { Locale } from "./i18n";
import type { LanguagePackId } from "./languagePackTypes";

export type AccountProfileDraft = {
  displayName: string;
  preferredLanguage: LanguagePackId;
  defaultGenerationLanguage: Locale;
  timeZone: string;
  outputPreference: NonNullable<ProfileInput["outputPreference"]>;
  includeSourceLinks: boolean;
};

type ProfileUserFields = Pick<
  User,
  "displayName" | "preferredLanguage" | "defaultGenerationLanguage" | "timeZone" | "outputPreference" | "includeSourceLinks"
>;

export function accountProfileDraftFromUser(user: ProfileUserFields | null): AccountProfileDraft {
  return {
    displayName: user?.displayName ?? "",
    preferredLanguage: user?.preferredLanguage ?? "en",
    defaultGenerationLanguage: user?.defaultGenerationLanguage ?? "en",
    timeZone: user?.timeZone ?? "UTC",
    outputPreference: user?.outputPreference ?? "balanced",
    includeSourceLinks: user?.includeSourceLinks ?? true,
  };
}

/**
 * Identity-only key for the profile form. It changes only when the
 * authenticated account changes (sign-in, sign-out, or switching accounts),
 * so a profile save echo — which updates the user's content but not their
 * identity — no longer remounts the form and discards edits the user made
 * while the save request was in flight. Resyncing those server content
 * changes is the job of `reconcileAccountProfileDraft`, which keeps any
 * field the user has since edited.
 */
export function accountProfileFormIdentityKey(user: Pick<User, "id"> | null): string {
  return user?.id ?? "signed-out";
}

export function accountProfileDraftsMatch(left: AccountProfileDraft, right: AccountProfileDraft): boolean {
  return (
    left.displayName === right.displayName &&
    left.preferredLanguage === right.preferredLanguage &&
    left.defaultGenerationLanguage === right.defaultGenerationLanguage &&
    left.timeZone === right.timeZone &&
    left.outputPreference === right.outputPreference &&
    left.includeSourceLinks === right.includeSourceLinks
  );
}

/**
 * Three-way reconciliation used when a new server snapshot (a save response
 * or a session refresh) arrives while the form holds local state.
 *
 * `baseline` is the server snapshot the local draft was last synced from. A
 * field the user has not touched since then (`local[field] === baseline[field]`)
 * adopts the incoming `server` value, so canonical server output — such as a
 * trimmed display name — still reaches the form. A field with a pending local
 * edit (`local[field] !== baseline[field]`) keeps the newer local value: a
 * stale save response must never overwrite an edit that happened after the
 * draft was submitted.
 */
export function reconcileAccountProfileDraft({
  local,
  baseline,
  server,
}: {
  local: AccountProfileDraft;
  baseline: AccountProfileDraft;
  server: AccountProfileDraft;
}): AccountProfileDraft {
  return {
    displayName: local.displayName !== baseline.displayName ? local.displayName : server.displayName,
    preferredLanguage: local.preferredLanguage !== baseline.preferredLanguage ? local.preferredLanguage : server.preferredLanguage,
    defaultGenerationLanguage:
      local.defaultGenerationLanguage !== baseline.defaultGenerationLanguage
        ? local.defaultGenerationLanguage
        : server.defaultGenerationLanguage,
    timeZone: local.timeZone !== baseline.timeZone ? local.timeZone : server.timeZone,
    outputPreference: local.outputPreference !== baseline.outputPreference ? local.outputPreference : server.outputPreference,
    includeSourceLinks: local.includeSourceLinks !== baseline.includeSourceLinks ? local.includeSourceLinks : server.includeSourceLinks,
  };
}

export function canonicalizeAccountProfileDraft(draft: AccountProfileDraft): AccountProfileDraft {
  return {
    ...draft,
    displayName: draft.displayName.trim(),
    timeZone: draft.timeZone.trim(),
  };
}
