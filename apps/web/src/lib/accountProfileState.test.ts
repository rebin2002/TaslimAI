import { describe, expect, it } from "vitest";
import {
  accountProfileDraftFromUser,
  accountProfileDraftsMatch,
  accountProfileFormIdentityKey,
  canonicalizeAccountProfileDraft,
  reconcileAccountProfileDraft,
  type AccountProfileDraft,
} from "./accountProfileState";

const owner = {
  id: "user-1",
  displayName: "Alice",
  preferredLanguage: "en" as const,
  defaultGenerationLanguage: "en" as const,
  timeZone: "UTC",
  outputPreference: "balanced" as const,
  includeSourceLinks: true,
};

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

describe("account profile form identity", () => {
  it("stays stable across save echoes so the mounted form is not discarded", () => {
    // A successful save returns the same account with new profile content.
    // The identity key must not change, otherwise the save response remounts
    // the form and wipes edits the user made while the request was in flight.
    const saved = accountProfileFormIdentityKey(owner);
    const echoed = { ...owner, displayName: "Alicia", timeZone: "Asia/Erbil" };
    expect(accountProfileFormIdentityKey(echoed)).toBe(saved);
  });

  it("changes across sign-out, sign-in, and account switches", () => {
    expect(accountProfileFormIdentityKey(null)).toBe("signed-out");
    expect(accountProfileFormIdentityKey(owner)).not.toBe("signed-out");
    expect(accountProfileFormIdentityKey({ ...owner, id: "user-2" })).not.toBe(accountProfileFormIdentityKey(owner));
  });
});

describe("account profile draft equality", () => {
  it("compares content across distinct objects", () => {
    const left: AccountProfileDraft = accountProfileDraftFromUser(owner);
    const right: AccountProfileDraft = { ...left };
    expect(accountProfileDraftsMatch(left, right)).toBe(true);
    expect(accountProfileDraftsMatch(left, { ...right, displayName: `${right.displayName} ` })).toBe(false);
    expect(accountProfileDraftsMatch(left, { ...right, includeSourceLinks: !right.includeSourceLinks })).toBe(false);
  });
});

describe("reconcileAccountProfileDraft", () => {
  const baseline: AccountProfileDraft = accountProfileDraftFromUser(owner);

  it("adopts the whole server snapshot when no field has a pending local edit", () => {
    const server: AccountProfileDraft = {
      ...baseline,
      displayName: "Alice Trimmed",
      timeZone: "Asia/Baghdad",
      outputPreference: "detailed",
    };
    expect(reconcileAccountProfileDraft({ local: baseline, baseline, server })).toEqual(server);
  });

  it("keeps a pending edit when a save echo for an older draft arrives late", () => {
    // The user submitted the baseline draft, then typed a newer display name
    // while the request was in flight. The save response echoes the submitted
    // (older) value plus a server-side timezone change.
    const local: AccountProfileDraft = { ...baseline, displayName: "Alicia" };
    const server: AccountProfileDraft = { ...baseline, displayName: "Alice", timeZone: "Asia/Erbil" };
    expect(reconcileAccountProfileDraft({ local, baseline, server })).toEqual({
      ...baseline,
      // The newer local edit wins: no client-side lost update.
      displayName: "Alicia",
      // Untouched fields still adopt the server echo.
      timeZone: "Asia/Erbil",
    });
  });

  it("adopts canonical server whitespace for fields the user has not touched", () => {
    const baselineUntrimmed: AccountProfileDraft = { ...baseline, displayName: "  Alice  " };
    const server: AccountProfileDraft = { ...baselineUntrimmed, displayName: "Alice" };
    expect(
      reconcileAccountProfileDraft({ local: baselineUntrimmed, baseline: baselineUntrimmed, server }).displayName,
    ).toBe("Alice");
  });

  it("keeps every pending edit while adopting every server-only change", () => {
    const local: AccountProfileDraft = {
      ...baseline,
      displayName: "Alicia",
      preferredLanguage: "ar",
      includeSourceLinks: false,
    };
    const server: AccountProfileDraft = {
      ...baseline,
      displayName: "Alice",
      defaultGenerationLanguage: "ku",
      timeZone: "Europe/London",
      outputPreference: "concise",
      includeSourceLinks: true,
    };
    expect(reconcileAccountProfileDraft({ local, baseline, server })).toEqual({
      displayName: "Alicia",
      preferredLanguage: "ar",
      defaultGenerationLanguage: "ku",
      timeZone: "Europe/London",
      outputPreference: "concise",
      includeSourceLinks: false,
    });
  });

  it("is a fixed point when the same baseline/server pair is reconciled again", () => {
    const local: AccountProfileDraft = { ...baseline, timeZone: "Asia/Erbil" };
    const server: AccountProfileDraft = { ...baseline, outputPreference: "concise" };
    const once = reconcileAccountProfileDraft({ local, baseline, server });
    const twice = reconcileAccountProfileDraft({ local: once, baseline, server });
    expect(twice).toEqual(once);
    expect(accountProfileDraftsMatch(twice, once)).toBe(true);
  });

  it("runs the full submit-echo-edit timeline without losing the newer edit", () => {
    // Synthetic reproduction of the lost-update race, using the same state
    // transitions AccountProfileForm performs across two saves.
    const synced0 = baseline;
    let synced: AccountProfileDraft = synced0;
    let local: AccountProfileDraft = { ...baseline, displayName: "  Alice  " };

    // 1. Save A submits the canonicalized draft; the request stays in flight.
    const submittedA = canonicalizeAccountProfileDraft(local);

    // 2. The user keeps typing while the request is in flight.
    local = { ...local, displayName: "Alicia" };

    // 3. The save A echo arrives late: it carries the submitted draft, never
    //    the mid-flight edit. The form reconciles instead of overwriting.
    const echoA = { ...synced0, displayName: submittedA.displayName, timeZone: "Asia/Baghdad" };
    local = reconcileAccountProfileDraft({ local, baseline: synced, server: echoA });
    synced = echoA;
    expect(local.displayName).toBe("Alicia");
    expect(local.timeZone).toBe("Asia/Baghdad");

    // 4. Save B submits the newer edit and its echo adopts it cleanly.
    const submittedB = canonicalizeAccountProfileDraft(local);
    const echoB = { ...echoA, displayName: submittedB.displayName };
    local = reconcileAccountProfileDraft({ local, baseline: synced, server: echoB });
    expect(local.displayName).toBe("Alicia");
    expect(accountProfileDraftsMatch(local, echoB)).toBe(true);
  });
});


describe("Chinese language-pack profile preservation", () => {
  it("keeps the zh-CN interface selection while generation language remains independent", () => {
    const user = { ...owner, preferredLanguage: "zh-CN" as const, defaultGenerationLanguage: "ar" as const };
    const draft = accountProfileDraftFromUser(user);
    expect(draft.preferredLanguage).toBe("zh-CN");
    expect(draft.defaultGenerationLanguage).toBe("ar");
    expect(reconcileAccountProfileDraft({ local: draft, baseline: draft, server: { ...draft, timeZone: "Asia/Baghdad" } })).toMatchObject({ preferredLanguage: "zh-CN", defaultGenerationLanguage: "ar", timeZone: "Asia/Baghdad" });
  });
});
