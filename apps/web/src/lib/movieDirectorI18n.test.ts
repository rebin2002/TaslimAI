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
  movieDirectorRoomDetail,
  movieDirectorRoomLabel,
  movieDirectorText,
} from "./movieDirectorI18n";

const syntheticPackValues: Record<string, string> = {
  "movieDirector.text.title": "Synthetic Film Director",
  "movieDirector.room.World": "Synthetic World",
  "movieDirector.roomDetail.noScene": "Synthetic scene guidance",
  "movieDirector.action.scene_planning": "Synthetic scene plan",
  "movieDirector.status.PendingApproval": "Synthetic review needed",
  "movieDirector.history.action_succeeded": "Synthetic action completed",
  "movieDirector.reason.high_story_importance": "Synthetic story importance",
  "movieDirector.prerequisite.guide_locked.label": "Synthetic guide lock",
  "movieDirector.prerequisite.guide_locked.unsatisfied": "Synthetic lock required",
  "movieDirector.quality.Cinematic": "Synthetic cinematic quality",
  "movieDirector.text.shot": "Synthetic shot {number}",
};

function translateSyntheticPack(key: string, variables?: Record<string, string>) {
  const value = syntheticPackValues[key] ?? key;
  return value.replace(/\{(\w+)\}/g, (_, name: string) => variables?.[name] ?? `{${name}}`);
}

function translateMissingPackKey(key: string) {
  return key;
}

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

  it("lets a synthetic installed pack override all Movie Director string catalogs", () => {
    expect(movieDirectorText("en", "title", {}, translateSyntheticPack)).toBe("Synthetic Film Director");
    expect(movieDirectorRoomLabel("en", "World", translateSyntheticPack)).toBe("Synthetic World");
    expect(movieDirectorRoomDetail("en", "noScene", {}, translateSyntheticPack)).toBe("Synthetic scene guidance");
    expect(movieDirectorText("en", "shot", { number: 4 }, translateSyntheticPack)).toBe("Synthetic shot 4");
    expect(movieDirectorActionLabel("en", "scene_planning", translateSyntheticPack)).toBe("Synthetic scene plan");
    expect(movieDirectorProposalStatusLabel("en", "PendingApproval", translateSyntheticPack)).toBe("Synthetic review needed");
    expect(movieDirectorHistoryLabel("en", "action_succeeded", translateSyntheticPack)).toBe("Synthetic action completed");
    expect(movieDirectorReasonLabel("en", "high_story_importance", translateSyntheticPack)).toBe("Synthetic story importance");
    expect(movieDirectorPrerequisite("en", "guide_locked", false, translateSyntheticPack)).toEqual({
      label: "Synthetic guide lock",
      detail: "Synthetic lock required",
    });
    expect(movieDirectorQualityLabel("en", "Cinematic", translateSyntheticPack)).toBe("Synthetic cinematic quality");
  });

  it("preserves existing en/ar/ku text when an installed pack has no matching key", () => {
    expect(movieDirectorText("ar", "title", {}, translateMissingPackKey)).toBe("مخرج تسليم");
    expect(movieDirectorRoomLabel("ku", "World", translateMissingPackKey)).toBe("جیهان");
    expect(movieDirectorActionLabel("ar", "scene_planning", translateMissingPackKey)).toBe("تخطيط المشهد");
  });

  it("keeps quality labels localized while preserving internal quality values", () => {
    expect(movieDirectorQualityLabel("en", "Cinematic")).toBe("Cinematic");
    expect(movieDirectorQualityLabel("ar", "Cinematic")).toBe("سينمائي");
    expect(movieDirectorQualityLabel("ku", "Cinematic")).toBe("سینەمایی");
  });
});
