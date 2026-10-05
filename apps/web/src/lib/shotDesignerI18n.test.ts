import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { locales } from "./i18n";
import {
  formatShotDesignerNumber,
  formatShotDesignerSequence,
  shotDesignerCapabilityLabel,
  shotDesignerControlLabel,
  shotDesignerIntentLabel,
  shotDesignerKeys,
  shotDesignerLocales,
  shotDesignerText,
} from "./shotDesignerI18n";

const globalStyles = readFileSync(new URL("../app/globals.css", import.meta.url), "utf8");

describe("Shot Designer localization", () => {
  it("keeps a complete English, Arabic, and Sorani catalog", () => {
    expect(shotDesignerLocales).toEqual(locales);
    for (const locale of locales) {
      for (const key of shotDesignerKeys) {
        expect(shotDesignerText(locale, key), `${locale}:${key}`).not.toBe("");
      }
    }
  });

  it("localizes intents, control labels, and capability states", () => {
    expect(shotDesignerIntentLabel("ar", "intimate")).toBe("حميمي");
    expect(shotDesignerIntentLabel("ku", "dynamic")).toBe("دینامیکی");
    expect(shotDesignerControlLabel("ar", "cameraMovement")).toBe("حركة الكاميرا");
    expect(shotDesignerControlLabel("ku", "compositionNotes")).toBe("پێکهاتە");
    expect(shotDesignerCapabilityLabel("ar", "Simulated/Post")).toBe("محاكاة/ما بعد الإنتاج");
    expect(shotDesignerCapabilityLabel("ku", "Unsupported")).toBe("پشتگیری نەکراوە");
  });

  it("interpolates localized revision copy and uses Arabic-Indic numerals for RTL locales", () => {
    expect(shotDesignerText("ar", "lockedRevision", { revision: "١٢" })).toBe("مقفل · المراجعة ١٢");
    expect(formatShotDesignerNumber("en", 12345)).toBe("12,345");
    expect(formatShotDesignerNumber("ar", 12345).replace(/[^٠-٩]/g, "")).toBe("١٢٣٤٥");
    expect(formatShotDesignerNumber("ku", 12345).replace(/[^٠-٩]/g, "")).toBe("١٢٣٤٥");
    expect(formatShotDesignerSequence("ar", 3).replace(/[^٠-٩]/g, "")).toBe("٠٣");
  });

  it("does not reintroduce the previous English-only Shot Designer chrome", () => {
    const source = readFileSync(new URL("../components/ShotDesigner.tsx", import.meta.url), "utf8");
    for (const legacyCopy of [
      "Shape the camera without needing to know lenses.",
      "Choose a feeling. Taslim fills the production direction.",
      "What happens in this shot?",
      "One locked continuity rule per line",
      "The Movie Guide remains unchanged; this saves a shot-level override.",
    ]) {
      expect(source).not.toContain(legacyCopy);
    }
    expect(source).toContain("useLocale");
    expect(source).toContain("shotDesignerText");
  });

  it("keeps Shot Designer control rows bidi-safe without breaking mobile stacking", () => {
    expect(globalStyles).toContain('[dir="rtl"] .movie-shot-designer-heading');
    expect(globalStyles).toContain('[dir="rtl"] .movie-shot-title');
    expect(globalStyles).toContain('[dir="rtl"] .movie-shot-list-item');
    expect(globalStyles).toContain("flex-direction: column");
  });
});
