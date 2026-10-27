import { describe, expect, it } from "vitest";
import { resolveDevelopmentHubCopy } from "./developmentHubCopy";

describe("development hub installable-pack copy", () => {
  it("uses optional copy supplied by a synthetic pack and falls back for missing keys", () => {
    const syntheticPack: Record<string, string> = {
      "developmentHub.eyebrow": "Synthetic pack workspace",
      "developmentHub.navigationLabel": "Synthetic pack tools",
      "developmentHub.open": "Synthetic open",
    };
    const translate = (key: string) => syntheticPack[key] ?? key;

    const copy = resolveDevelopmentHubCopy("en", translate);

    expect(copy.eyebrow).toBe("Synthetic pack workspace");
    expect(copy.navigationLabel).toBe("Synthetic pack tools");
    expect(copy.open).toBe("Synthetic open");
    expect(copy.description).toBe("Move from learning to shipping with a clear path through the development workspace.");
    expect(copy.backHome).toBe("Back to home");
  });

  it("preserves current English, Arabic, and Sorani copy when the pack has no optional keys", () => {
    const missingKey = (key: string) => key;

    expect(resolveDevelopmentHubCopy("en", missingKey)).toMatchObject({
      eyebrow: "Development workspace",
      navigationLabel: "Development tools",
      open: "Open",
      backHome: "Back to home",
    });
    expect(resolveDevelopmentHubCopy("ar", missingKey)).toMatchObject({
      eyebrow: "مساحة عمل التطوير",
      navigationLabel: "أدوات التطوير",
      open: "فتح",
      backHome: "العودة إلى الرئيسية",
    });
    expect(resolveDevelopmentHubCopy("ku", missingKey)).toMatchObject({
      eyebrow: "شوێنکاریی گەشەپێدان",
      navigationLabel: "ئامرازەکانی گەشەپێدان",
      open: "کردنەوە",
      backHome: "گەڕانەوە بۆ سەرەکی",
    });
  });
});
