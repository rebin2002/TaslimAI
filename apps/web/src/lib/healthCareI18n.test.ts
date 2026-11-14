import { describe, expect, it } from "vitest";
import { locales } from "@/lib/i18n";
import { healthCareCopy, healthCareDirection, healthCareLanguageTag } from "./healthCareI18n";

describe("Health Care feature-local copy", () => {
  it("provides complete privacy and safety copy in every supported locale", () => {
    for (const locale of locales) {
      const copy = healthCareCopy(locale);
      const required = [
        copy.category,
        copy.eyebrow,
        copy.title,
        copy.description,
        copy.status,
        copy.plannedHeading,
        copy.actionUnavailable,
        copy.privacyLabel,
        copy.privacyNote,
        copy.safetyLabel,
        copy.safetyNote,
        copy.backToCreate,
        copy.errorEyebrow,
        copy.errorTitle,
        copy.errorDescription,
        copy.retry,
        ...copy.features.map((feature) => feature.label),
      ];

      expect(required.every((value) => value.trim().length > 0), `copy is complete for ${locale}`).toBe(true);
      expect(copy.features).toHaveLength(4);
      expect(JSON.stringify(copy)).not.toContain("Health Studio");
      expect(copy.privacyNote).toMatch(/external|خارجية|دەرەکییە/);
      expect(copy.safetyNote).toMatch(/diagnosis|تشخيص|دەستنیشانکردن/);
    }
  });

  it("uses the requested module name and correct RTL language tags", () => {
    expect(healthCareCopy("en").title).toBe("Health Care");
    expect(healthCareCopy("ar").title).toBe("الرعاية الصحية");
    expect(healthCareCopy("ku").title).toBe("چاودێریی تەندروستی");
    expect(healthCareDirection("en")).toBe("ltr");
    expect(healthCareDirection("ar")).toBe("rtl");
    expect(healthCareDirection("ku")).toBe("rtl");
    expect(healthCareLanguageTag("ku")).toBe("ckb");
  });
});
