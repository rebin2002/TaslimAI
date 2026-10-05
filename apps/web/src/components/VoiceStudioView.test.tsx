import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { LocaleProvider } from "./LocaleProvider";
import { VoiceAudioPlayer, voiceLanguageTag, voiceTextDirection } from "./VoiceStudioView";
import { translate } from "../lib/i18n";

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
});
