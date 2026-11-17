import { describe, expect, it } from "vitest";
import { profileAvatarFallbackInitial, profileAvatarInitial } from "./profileAvatar";

/**
 * Pins the derivation of the profile avatar thumbnail. Every fixture here is
 * synthetic: no account, workspace, transport, or generation provider is
 * involved, and nothing reads a stored profile.
 */
function isBrokenMark(mark: string): boolean {
  // Half a surrogate pair is what the reader sees as a replacement box.
  return mark.length === 1 && mark.charCodeAt(0) >= 0xd800 && mark.charCodeAt(0) <= 0xdfff;
}

describe("profile avatar thumbnail initial", () => {
  it("keeps a name whose first character sits above the Basic Multilingual Plane whole", () => {
    const mark = profileAvatarInitial({ displayName: "\u{10400}lcorn" }, "en");
    expect(isBrokenMark(mark)).toBe(false);
    expect(mark).toBe("\u{10400}");
    expect(Array.from(mark)).toHaveLength(1);
  });

  it("keeps an emoji-led name as one character instead of its first code unit", () => {
    expect(profileAvatarInitial({ displayName: "\u{1F600} Alex" }, "en")).toBe("\u{1F600}");
  });

  it("keeps a joined emoji family as the single mark the reader sees", () => {
    const family = "\u{1F468}\u200D\u{1F469}\u200D\u{1F467}";
    expect(profileAvatarInitial({ displayName: `${family} Kim` }, "en")).toBe(family);
  });

  it("ignores surrounding whitespace so both avatar marks agree", () => {
    expect(profileAvatarInitial({ displayName: "  Rebin  " }, "en")).toBe("R");
  });

  it("falls back to the brand mark when there is nothing to draw", () => {
    expect(profileAvatarInitial({ displayName: "   " }, "en")).toBe(profileAvatarFallbackInitial);
    expect(profileAvatarInitial({ displayName: "" }, "en")).toBe(profileAvatarFallbackInitial);
    expect(profileAvatarInitial(null, "en")).toBe(profileAvatarFallbackInitial);
  });

  it("drops a stored value cut mid surrogate pair instead of rendering a broken glyph", () => {
    expect(profileAvatarInitial({ displayName: "\u{DC00}Alora" }, "en")).toBe("A");
    expect(profileAvatarInitial({ displayName: "\u{D800}" }, "en")).toBe(profileAvatarFallbackInitial);
  });

  it("re-derives from the current session snapshot so a changed name changes the mark", () => {
    const before = { displayName: "\u{10400}lcorn" };
    const after = { displayName: "Dellam" };
    expect(profileAvatarInitial(before, "en")).toBe("\u{10400}");
    expect(profileAvatarInitial(after, "en")).toBe("D");
    // Switching account must never keep the previous account's mark.
    expect(profileAvatarInitial({ displayName: "Sara" }, "ku")).toBe("S");
  });

  it("cases the mark in the active interface language", () => {
    expect(profileAvatarInitial({ displayName: "abe" }, "en")).toBe("A");
    // The canonical tag for the Kurdish interface is ckb, not the key itself.
    expect(profileAvatarInitial({ displayName: "\u{0633}\u0627\u0631\u0629" }, "ar")).toBe("\u{0633}");
  });

  it("survives a malformed language tag without blanking the header", () => {
    expect(profileAvatarInitial({ displayName: "abe" }, "not a locale!!")).toBe("A");
    expect(profileAvatarInitial({ displayName: "abe" })).toBe("A");
  });
});
