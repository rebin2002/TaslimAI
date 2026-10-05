import { describe, expect, it } from "vitest";
import { accountProfileDraftFromUser, accountProfileSessionKey, canonicalizeAccountProfileDraft } from "./accountProfileState";

describe("account profile state", () => {
  it("hydrates every editable field from the canonical authenticated user", () => {
    expect(accountProfileDraftFromUser({
      displayName: "Arabic Owner",
      preferredLanguage: "ar",
      defaultGenerationLanguage: "ku",
      timeZone: "Asia/Baghdad",
      outputPreference: "detailed",
      includeSourceLinks: false,
    })).toEqual({
      displayName: "Arabic Owner",
      preferredLanguage: "ar",
      defaultGenerationLanguage: "ku",
      timeZone: "Asia/Baghdad",
      outputPreference: "detailed",
      includeSourceLinks: false,
    });
  });

  it("provides safe defaults before an authenticated profile is available", () => {
    expect(accountProfileDraftFromUser(null)).toEqual({
      displayName: "",
      preferredLanguage: "en",
      defaultGenerationLanguage: "en",
      timeZone: "UTC",
      outputPreference: "balanced",
      includeSourceLinks: true,
    });
  });

  it("changes the form identity across logout/login and canonical profile transitions", () => {
    const user = {
      id: "user-1",
      displayName: "Owner",
      preferredLanguage: "en" as const,
      defaultGenerationLanguage: "en" as const,
      timeZone: "UTC",
      outputPreference: "balanced" as const,
      includeSourceLinks: true,
    };
    expect(accountProfileSessionKey(null)).toBe("signed-out");
    expect(accountProfileSessionKey(user)).not.toBe(accountProfileSessionKey({ ...user, id: "user-2" }));
    expect(accountProfileSessionKey(user)).not.toBe(accountProfileSessionKey({ ...user, displayName: "Updated Owner" }));
  });

  it("canonicalizes editable text before profile persistence", () => {
    expect(canonicalizeAccountProfileDraft({
      displayName: "  Owner  ",
      preferredLanguage: "en",
      defaultGenerationLanguage: "ar",
      timeZone: "  Asia/Erbil  ",
      outputPreference: "concise",
      includeSourceLinks: true,
    })).toEqual({
      displayName: "Owner",
      preferredLanguage: "en",
      defaultGenerationLanguage: "ar",
      timeZone: "Asia/Erbil",
      outputPreference: "concise",
      includeSourceLinks: true,
    });
  });
});
