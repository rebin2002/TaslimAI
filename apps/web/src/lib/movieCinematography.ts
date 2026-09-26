import type {
  CinematographyIntentSelection,
  CinematographyPreset,
  MovieCinematographyBible,
} from "./api";

export const CINEMATOGRAPHY_INTENTS = [
  "intimate",
  "natural",
  "epic",
  "dynamic",
] as const;
export type CinematographyIntent = (typeof CINEMATOGRAPHY_INTENTS)[number];

export const CINEMATOGRAPHY_CONTROL_KEYS = [
  "shotSize",
  "focalLength",
  "lensIntent",
  "apertureDepthOfField",
  "cameraAngle",
  "cameraMovement",
  "frameRateIntent",
  "lighting",
  "paletteLook",
  "compositionNotes",
] as const;

export type CinematographyControlKey =
  (typeof CINEMATOGRAPHY_CONTROL_KEYS)[number];

export function presetForIntent(
  presets: CinematographyPreset[],
  intent: string | null | undefined,
) {
  return presets.find(
    (preset) => preset.intent?.toLowerCase() === intent?.toLowerCase(),
  );
}

export function selectionFromPreset(
  preset: CinematographyPreset,
  previous: CinematographyIntentSelection = {},
): CinematographyIntentSelection {
  return {
    ...previous,
    intent: preset.intent,
    presetId: preset.id,
    capabilityReferences: preset.capabilityReferences ?? null,
    shotSize: preset.shotSize,
    focalLength: preset.focalLength,
    lensIntent: preset.lensIntent,
    apertureDepthOfField: preset.apertureDepthOfField,
    cameraAngle: preset.cameraAngle,
    cameraMovement: preset.cameraMovement,
    frameRateIntent: preset.frameRateIntent,
    lighting: preset.lighting,
    paletteLook: preset.paletteLook,
    compositionNotes: preset.compositionNotes,
  };
}

export function emptyCinematographySelection(
  intent: CinematographyIntent = "natural",
): CinematographyIntentSelection {
  return {
    intent,
    presetId: null,
    capabilityReferences: null,
    shotSize: null,
    focalLength: null,
    lensIntent: null,
    apertureDepthOfField: null,
    cameraAngle: null,
    cameraMovement: null,
    frameRateIntent: null,
    lighting: null,
    paletteLook: null,
    compositionNotes: null,
  };
}

export function parseCinematographyJson(
  json: string | null | undefined,
): CinematographyIntentSelection | null {
  if (!json?.trim()) return null;
  try {
    const parsed: unknown = JSON.parse(json);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed)
      ? (parsed as CinematographyIntentSelection)
      : null;
  } catch {
    return null;
  }
}

export function guidePreset(
  guide: MovieCinematographyBible | null | undefined,
  presets: CinematographyPreset[],
) {
  return (
    presetForIntent(presets, guide?.intent) ??
    presets.find((preset) => preset.id === guide?.presetId)
  );
}

export function capabilityForField(
  selection: CinematographyIntentSelection | null | undefined,
  field: string,
) {
  return selection?.capabilityReferences?.find(
    (reference) => reference.field === field,
  );
}

export function intentLabel(intent: string | null | undefined) {
  if (!intent) return "Not set";
  return intent.charAt(0).toUpperCase() + intent.slice(1).toLowerCase();
}
