import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { MovieGuide, MovieScene } from "@/lib/api";
import { ShotDesigner } from "./ShotDesigner";

const localeState = vi.hoisted(() => ({ current: "ar" as "ar" | "ku" }));

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: localeState.current }),
}));

const mixedScriptDescription = "Camera A — لقطة ١";
const scene = {
  id: "scene-rtl",
  shots: [
    {
      id: "shot-rtl",
      sequence: 1,
      description: mixedScriptDescription,
      cinematographyJson: null,
    },
  ],
} as unknown as MovieScene;
const guide = {} as MovieGuide;

describe.each(["ar", "ku"] as const)("ShotDesigner %s RTL content", (locale) => {
  it("isolates user-authored mixed-script shot descriptions from the surrounding direction", () => {
    localeState.current = locale;
    const markup = renderToStaticMarkup(
      createElement(
        "div",
        { lang: locale, dir: "rtl" },
        createElement(ShotDesigner, {
          scene,
          guide,
          presets: [],
          onAddShot: async () => true,
        }),
      ),
    );

    expect(markup).toContain(`<bdi dir="auto">${mixedScriptDescription}</bdi>`);
  });
});
