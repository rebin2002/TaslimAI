import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

/**
 * Pins the state-race boundary of the account profile form. The save
 * response for an older draft must never overwrite a newer local edit, so
 * the component is required to (a) remount only on account-identity changes
 * and (b) reconcile incoming server snapshots field by field instead of
 * writing the submitted draft back into per-field state. The reconciliation
 * itself is exercised by src/lib/accountProfileState.test.ts.
 */
const accountViewSource = readFileSync(new URL("./AccountView.tsx", import.meta.url), "utf8");

describe("account profile form lost-update boundary", () => {
  it("keys the form on account identity only, never on profile content", () => {
    expect(accountViewSource).toContain("key={accountProfileFormIdentityKey(user)}");
    // The content-addressed key remounted the form on every save echo and
    // discarded mid-flight edits; it must not come back.
    expect(accountViewSource).not.toContain("accountProfileSessionKey");
  });

  it("reconciles server snapshots instead of discarding pending edits", () => {
    expect(accountViewSource).toContain("reconcileAccountProfileDraft(");
    expect(accountViewSource).toContain("accountProfileDraftsMatch(");
  });

  it("never writes the submitted draft back over the live field state", () => {
    // The save success path used to set the display name and timezone from
    // the submitted draft, clobbering edits typed while the request was in
    // flight. All profile fields now live in one reconciled draft.
    expect(accountViewSource).not.toMatch(/setDisplayName\(/);
    expect(accountViewSource).not.toMatch(/setPreferredLanguage\(/);
    expect(accountViewSource).not.toMatch(/setDefaultGenerationLanguage\(/);
    expect(accountViewSource).not.toMatch(/setTimeZone\(/);
    expect(accountViewSource).not.toMatch(/setOutputPreference\(/);
    expect(accountViewSource).not.toMatch(/setIncludeSourceLinks\(/);
    expect(accountViewSource).toContain("canonicalizeAccountProfileDraft(draft)");
  });

  it("keeps the save button gated while a request is in flight", () => {
    expect(accountViewSource).toContain('disabled={profileState === "saving"}');
  });
});
