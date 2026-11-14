import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { LocaleProvider } from "./LocaleProvider";
import { VoiceAudioPlayer, nextVoiceChoiceIndex, voiceLanguageTag, voiceTextDirection } from "./VoiceStudioView";
import { translate } from "../lib/i18n";

const voiceSource = readFileSync(new URL("./VoiceStudioView.tsx", import.meta.url), "utf8");
const globalStyles = readFileSync(new URL("../app/globals.css", import.meta.url), "utf8");

describe("Voice Studio accessibility", () => {
  it("gives the custom audio controls localized names and a readable progress value", () => {
    const html = renderToStaticMarkup(
      <LocaleProvider>
        <VoiceAudioPlayer src="/api/assets/asset-1/download?inline=true" label="Ready to listen" durationMilliseconds={120_000} />
      </LocaleProvider>,
    );

    expect(html).toContain('aria-label="Play Ready to listen"');
    expect(html).toContain('aria-label="Ready to listen playback progress"');
    expect(html).toContain('aria-valuemin="0"');
    expect(html).toContain('aria-valuemax="120"');
    expect(html).toContain('aria-valuenow="0"');
    expect(html).toContain('aria-valuetext="Playback: 0:00 of 2:00"');
    expect(html).toContain('aria-hidden="true"');
  });

  it("keeps generated-script direction and language metadata aligned for RTL speech", () => {
    expect(voiceLanguageTag("en")).toBe("en");
    expect(voiceLanguageTag("ar")).toBe("ar");
    expect(voiceLanguageTag("ku")).toBe("ku-Arab");
    expect(voiceTextDirection("en")).toBe("ltr");
    expect(voiceTextDirection("ar")).toBe("rtl");
    expect(voiceTextDirection("ku")).toBe("rtl");
  });

  it("provides playback labels in Arabic and Kurdish Sorani", () => {
    expect(translate("ar", "voice.play", { label: "جاهز" })).toBe("تشغيل جاهز");
    expect(translate("ku", "voice.play", { label: "ئامادە" })).toBe("یاری‌کردنی ئامادە");
    expect(translate("ar", "voice.progressValue", { current: "0:00", duration: "2:00" })).toContain("0:00");
    expect(translate("ku", "voice.progressValue", { current: "0:00", duration: "2:00" })).toContain("2:00");
  });

  it("supports arrow and boundary keys without adding every choice to the tab order", () => {
    expect(nextVoiceChoiceIndex(0, "ArrowRight", 3)).toBe(1);
    expect(nextVoiceChoiceIndex(0, "ArrowLeft", 3)).toBe(2);
    expect(nextVoiceChoiceIndex(1, "Home", 3)).toBe(0);
    expect(nextVoiceChoiceIndex(1, "End", 3)).toBe(2);
    expect(nextVoiceChoiceIndex(1, "Enter", 3)).toBeNull();
    expect(voiceSource).toContain('tabIndex={language === value ? 0 : -1}');
    expect(voiceSource).toContain('onKeyDown={(event) => handleVoiceChoiceKeyDown(event, languages, value, setLanguage)}');
  });

  it("keeps the final dark-hero title rule on the light foreground", () => {
    const parallelVoiceStudioBaseStyles = globalStyles.slice(globalStyles.indexOf("/* Parallel Voice Studio */")).split("@media")[0];
    expect(parallelVoiceStudioBaseStyles).toMatch(/\.voice-studio-header h1\s*\{[^}]*color: var\(--white\);/);
  });
});
