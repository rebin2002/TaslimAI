/**
 * Provider-free pins for the Movie Studio shot-edit surface's input handling.
 *
 * Scope: the Shot Designer's own client-side validation of a shot draft and the
 * localized message it must give for each meaningful invalid entry. The shot
 * metadata bounds the API stores (#972), timeline clip authorization (#965) and
 * script autosave fencing (#979) are separate lanes and are not touched here.
 * No provider, no network, no real project data: the designer is a pure function
 * of its draft, so the messaging is asserted on rendered markup and on the pure
 * validators that gate the save.
 */
import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { ShotDesigner, ShotDraftIssues } from "./ShotDesigner";
import { LocaleProvider } from "./LocaleProvider";
import { shotDesignerKeys, shotDesignerLocales, shotDesignerText } from "@/lib/shotDesignerI18n";
import type { CinematographyPreset, MovieGuide, MovieScene } from "@/lib/api";
import type { ShotDraftIssue } from "@/lib/shotDraftValidation";

const sceneId = "scene-synthetic-1";
const designerSource = readFileSync(new URL("./ShotDesigner.tsx", import.meta.url), "utf8");

function scene(overrides: Partial<MovieScene> = {}): MovieScene {
  return {
    id: sceneId,
    sequence: 1,
    title: "Morning street",
    summary: "Establish the street before Mara arrives.",
    durationSeconds: 24,
    continuityNotes: null,
    narration: null,
    dialogue: null,
    shots: [],
    clips: [],
    ...overrides,
  };
}

function guide(overrides: Partial<MovieGuide> = {}): MovieGuide {
  return {
    id: "guide-synthetic-1",
    visualLanguage: "Restrained dawn palette",
    cameraLanguage: "Eye level, handheld",
    colorAndLighting: "Cool, practical sources",
    soundAndNarration: "Street ambience under narration",
    continuityRules: "Mara carries the same canvas bag",
    updatedAt: "2026-11-17T00:00:00.000Z",
    ...overrides,
  };
}

function preset(overrides: Partial<CinematographyPreset> = {}): CinematographyPreset {
  return {
    id: "preset-natural",
    name: "Natural",
    summary: "Documentary warmth with soft contrast.",
    intent: "natural",
    shotSize: "Medium-wide",
    focalLength: "35mm equivalent",
    lensIntent: "Prime, gentle falloff",
    apertureDepthOfField: "Shallow enough to separate Mara",
    cameraAngle: "Eye level",
    cameraMovement: "Slow push in",
    frameRateIntent: "24fps",
    lighting: "Practical dawn light",
    paletteLook: "Cool amber",
    compositionNotes: "Leave negative space ahead of the walk",
    ...overrides,
  };
}

const render = (props: Partial<Parameters<typeof ShotDesigner>[0]> = {}) =>
  renderToStaticMarkup(
    <LocaleProvider>
      <ShotDesigner
        scene={scene()}
        guide={guide()}
        presets={[preset()]}
        saving={false}
        onAddShot={async () => true}
        {...props}
      />
    </LocaleProvider>,
  );

const issues = (...entries: ShotDraftIssue[]) =>
  renderToStaticMarkup(<ShotDraftIssues locale="en" issues={entries} />);

describe("Shot Designer invalid input messaging", () => {
  it("states the accepted duration range before the customer has to guess it", () => {
    const html = render();
    // React keeps the JSX casing of these attributes in its markup, so the pin
    // matches either spelling rather than the renderer's serialisation choice.
    expect(html).toMatch(/inputmode="numeric"/i);
    expect(html).toContain('step="1"');
    expect(html).toContain("Whole seconds between 1 and 3,600.");
    // The hint is announced with the control it describes.
    expect(html).toMatch(new RegExp(`aria-describedby="[^"]*shot-duration-hint-${sceneId}[^"]*"`));
  });

  it("renders no fault for a pristine draft and keeps the save reachable", () => {
    const html = render();
    expect(html).not.toContain('role="alert"');
    // A disabled control explains nothing. The save stays reachable so the
    // designer can name the field that is missing instead of going quiet.
    expect(html).not.toContain("disabled");
  });

  it("names the violated bound for every invalid draft entry", () => {
    const markup = issues(
      { field: "description", code: "required" },
      { field: "duration", code: "notWhole" },
      { field: "duration", code: "tooLarge", limit: 3600 },
      { field: "duration", code: "notNumber" },
      { field: "shotSize", code: "tooLong", limit: 600, characters: 601 },
      { field: "continuityConstraints", code: "tooManyLines", limit: 24, characters: 25 },
      { field: "continuityConstraints", code: "tooLong", limit: 800, characters: 801 },
      { field: "notes", code: "tooLong", limit: 4000, characters: 4001 },
    );
    expect(markup).toContain('role="alert"');
    expect(markup).toContain("Add a shot description so this shot is recognisable later.");
    expect(markup).toContain("Shot duration must be a whole number of seconds.");
    expect(markup).toContain("Shot duration cannot exceed 3,600 seconds.");
    expect(markup).toContain("Enter the duration as a number of whole seconds, for example 6.");
    expect(markup).toContain("Shot size must be 600 characters or fewer.");
    expect(markup).toContain("Continuity constraints accept at most 24 rules; this draft has 25.");
    expect(markup).toContain("Each continuity constraint must be 800 characters or fewer.");
    expect(markup).toContain("Direction notes must be 4,000 characters or fewer.");
    // No placeholder copy, and no message that a value is wrong without saying
    // what to do about it.
    expect(markup).not.toMatch(/invalid input/i);
    expect(markup).not.toContain("{");
  });

  it("keeps a valid entry free of any message", () => {
    expect(issues()).toBe("");
  });

  it("translates every shot-draft message into Arabic and Sorani", () => {
    for (const locale of shotDesignerLocales) {
      for (const key of shotDesignerKeys) {
        expect(shotDesignerText(locale, key), `${locale}:${key}`).toBeTruthy();
      }
    }
    for (const locale of ["ar", "ku"] as const) {
      expect(shotDesignerText(locale, "durationHint", { minimum: "1", maximum: "3600" })).not.toBe(
        shotDesignerText("en", "durationHint", { minimum: "1", maximum: "3600" }),
      );
      expect(shotDesignerText(locale, "issueDurationNotWhole")).not.toBe(
        shotDesignerText("en", "issueDurationNotWhole"),
      );
      expect(shotDesignerText(locale, "issueDescriptionRequired")).not.toBe(
        shotDesignerText("en", "issueDescriptionRequired"),
      );
    }
    // Numbers follow the locale's own digits, matching the rest of the surface.
    expect(shotDesignerText("ar", "durationHint", { minimum: "١", maximum: "٣٬٦٠٠" })).toContain("٣٬٦٠٠");
  });

  it("gates the save on the validated draft instead of dropping the value silently", () => {
    // The payload can only ever carry a duration the API accepts, and a blocked
    // save always routes through the messages above.
    expect(designerSource).toContain("parseShotDurationInput(");
    expect(designerSource).toContain("validateShotDraft(");
    expect(designerSource).toContain("visibleShotDraftIssues(");
    expect(designerSource).not.toContain("disabled={saving || !draft.description.trim()}");
    expect(designerSource).not.toContain('if (!draft.description.trim() || saving) return;');
    // Bound values come from the shared contract, never retyped per element.
    expect(designerSource).toContain("shotDraftLimits.durationSecondsMaximum");
    expect(designerSource).not.toContain("max={3600}");
  });
});
