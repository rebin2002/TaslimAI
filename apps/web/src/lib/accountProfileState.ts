import type { ProfileInput, User } from "./api";
import type { Locale } from "./i18n";

export type AccountProfileDraft = {
  displayName: string;
  preferredLanguage: Locale;
  defaultGenerationLanguage: Locale;
  timeZone: string;
  outputPreference: NonNullable<ProfileInput["outputPreference"]>;
  includeSourceLinks: boolean;
};

type ProfileUserFields = Pick<
  User,
  "displayName" | "preferredLanguage" | "defaultGenerationLanguage" | "timeZone" | "outputPreference" | "includeSourceLinks"
>;

type ProfileSessionUser = Pick<User, "id"> & ProfileUserFields;

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

export function accountProfileSessionKey(user: ProfileSessionUser | null): string {
  if (!user) return "signed-out";
  return [user.id, user.displayName, user.preferredLanguage, user.defaultGenerationLanguage, user.timeZone, user.outputPreference, user.includeSourceLinks].join(":");
}

export function canonicalizeAccountProfileDraft(draft: AccountProfileDraft): AccountProfileDraft {
  return {
    ...draft,
    displayName: draft.displayName.trim(),
    timeZone: draft.timeZone.trim(),
  };
}
