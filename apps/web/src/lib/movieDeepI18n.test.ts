import { describe, expect, it } from "vitest";
import { localeDirection, locales, translate } from "./i18n";

const movieDeepKeys = [
  "movieDeep.worldRooms",
  "movieDeep.productionIdentity",
  "movieDeep.lockedFacts",
  "movieDeep.productionNotes",
  "movieDeep.environment",
  "movieDeep.locationRelationship",
  "movieDeep.continuityInformation",
  "movieDeep.geographySheet",
  "movieDeep.saveSheet",
  "movieDeep.approveSheet",
  "movieDeep.finishSaved",
  "movieDeep.finishDirection",
  "movieDeep.chooseFinish",
  "movieDeep.sourceIntent",
  "movieDeep.masterIntent",
  "movieDeep.qualityCheck",
  "movieDeep.selectedTakeReady",
  "movieDeep.noTakeSelected",
  "movieDeep.storyboardReview",
  "movieDeep.editSequence",
  "movieDeep.timelineNeedsFootage",
  "movieDeep.reviewQueue",
  "movieDeep.findSmallerFix",
  "movieDeep.filterShots",
] as const;

describe("Movie Studio deep localization", () => {
  it("keeps the remaining body labels translated in every supported locale", () => {
    for (const locale of locales) {
      for (const key of movieDeepKeys) expect(translate(locale, key)).not.toBe(key);
    }
  });

  it("keeps Movie Studio RTL locales right-to-left", () => {
    expect(localeDirection("en")).toBe("ltr");
    expect(localeDirection("ar")).toBe("rtl");
    expect(localeDirection("ku")).toBe("rtl");
  });
});
