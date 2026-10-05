import { describe, expect, it } from "vitest";
import { locales } from "./i18n";
import {
  formatMovieDirectorDate,
  formatMovieDirectorNumber,
  movieDirectorActionLabel,
  movieDirectorHistoryLabel,
  movieDirectorKeys,
  movieDirectorLocales,
  movieDirectorPrerequisite,
  movieDirectorProposalStatusLabel,
  movieDirectorQualityLabel,
  movieDirectorReasonLabel,
  movieDirectorRoomLabel,
  movieDirectorText,
} from "./movieDirectorI18n";

describe("Movie Director localization", () => {
  it("keeps a complete English, Arabic, and Sorani catalog", () => {
    expect(movieDirectorLocales).toEqual(locales);
    for (const locale of locales) {
      for (const key of movieDirectorKeys) {
        expect(movieDirectorText(locale, key), `${locale}:${key}`).not.toBe(key);
      }
    }
  });

  it("translates room, action, status, history, and prerequisite labels", () => {
    expect(movieDirectorRoomLabel("ar", "Storyboard")).toBe("لوحة القصة");
    expect(movieDirectorRoomLabel("ku", "Production")).toBe("بەرهەمهێنان");
    expect(movieDirectorActionLabel("ar", "shot_planning")).toBe("تخطيط اللقطة");
    expect(movieDirectorProposalStatusLabel("ku", "PendingApproval")).toBe("پێویستی بە پێداچوونەوەیە");
    expect(movieDirectorHistoryLabel("ar", "action_succeeded")).toBe("اكتمل الإجراء");
    expect(movieDirectorReasonLabel("ku", "high_story_importance")).toBe("گرنگیی بەرزی چیرۆک");
    expect(movieDirectorPrerequisite("ku", "guide_locked", false).detail).toBe("سەرەتا ڕێبەری فیلمی ئێستا قفل بکە.");
  });

  it("uses locale-aware numbers and dates for RTL locales", () => {
    expect(formatMovieDirectorNumber("en", 12345)).toBe("12,345");
    expect(formatMovieDirectorNumber("ar", 12345).replace(/[^٠-٩]/g, "")).toBe("١٢٣٤٥");
    expect(formatMovieDirectorNumber("ku", 12345).replace(/[^٠-٩]/g, "")).toBe("١٢٣٤٥");
    expect(formatMovieDirectorDate("en", "2026-01-02T13:04:00Z")).not.toBe(formatMovieDirectorDate("ar", "2026-01-02T13:04:00Z"));
  });

  it("keeps quality labels localized while preserving internal quality values", () => {
    expect(movieDirectorQualityLabel("en", "Cinematic")).toBe("Cinematic");
    expect(movieDirectorQualityLabel("ar", "Cinematic")).toBe("سينمائي");
    expect(movieDirectorQualityLabel("ku", "Cinematic")).toBe("سینەمایی");
  });
});
