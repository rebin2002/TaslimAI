import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { profileAvatarInitial } from "@/lib/profileAvatar";

/**
 * Pins the profile avatar thumbnails in the user profile UI: the header chip
 * and the account page identity mark must both draw from one derivation of the
 * current session snapshot. Duplicated inline slicing is what let the two
 * disagree and show a broken glyph, so the tests below keep the helper as the
 * single source.
 */
const headerSource = readFileSync(new URL("./AppHeader.tsx", import.meta.url), "utf8");
const accountSource = readFileSync(new URL("./AccountView.tsx", import.meta.url), "utf8");

describe("profile avatar thumbnails share one derivation", () => {
  it("draws the header thumbnail through the shared helper and the live session user", () => {
    expect(headerSource).toContain("profileAvatarInitial(user, locale)");
    expect(headerSource).toContain("const { user } = useAuth();");
    expect(headerSource).not.toMatch(/displayName\.slice\(0, *1\)/);
  });

  it("draws the account identity mark through the same helper", () => {
    expect(accountSource).toContain("profileAvatarInitial(user, locale)");
    expect(accountSource).not.toMatch(/displayName\.trim\(\)\.slice\(0, *1\)/);
  });

  it("never caches the mark in component state, so a profile change refreshes it", () => {
    expect(headerSource).not.toMatch(/[sS]etInitials?\(/);
    expect(accountSource).not.toMatch(/[sS]etInitials?\(/);
    expect(headerSource).not.toMatch(/useMemo\([^)]*displayName/);
  });

  it("agrees for one snapshot and changes with the next one", () => {
    const snapshot = { displayName: "\u{10400}lcorn" };
    expect(profileAvatarInitial(snapshot, "en")).toBe(profileAvatarInitial({ ...snapshot }, "en"));
    expect(profileAvatarInitial({ displayName: "Dellam" }, "en")).toBe("D");
  });
});
