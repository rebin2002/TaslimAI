import { describe, expect, it } from "vitest";
import { translate } from "./i18n";
import {
  emptyCinematographySelection,
  parseCinematographyJson,
  presetForIntent,
  selectionFromPreset,
} from "./movieCinematography";
import type { CinematographyPreset } from "./api";

const naturalPreset: CinematographyPreset = {
  id: "natural-observation",
  name: "Natural",
  intent: "natural",
  summary: "Observational coverage.",
  shotSize: "Medium to medium wide",
  focalLength: "28–50mm equivalent",
  lensIntent: "Documentary-normal perspective",
  apertureDepthOfField: "Moderate depth of field",
  cameraAngle: "Eye level",
  cameraMovement: "Stable handheld",
  frameRateIntent: "24 fps",
  lighting: "Available light",
  paletteLook: "True-to-life color",
  compositionNotes: "Protect spatial relationships.",
  capabilityReferences: [
    {
      field: "focalLength",
      classification: "Translated",
      rationale: "Visual lens intent.",
    },
  ],
};

describe("Movie Studio Shot Designer contract language", () => {
  it("keeps the normal-mode intent choices explicit", () => {
    expect(
      ["intimate", "natural", "epic", "dynamic"].map((intent) =>
        translate("en", `movie.intent.${intent}`),
      ),
    ).toEqual(["Intimate", "Natural", "Epic", "Dynamic"]);
  });

  it("labels advanced controls as production intent instead of native capability", () => {
    expect(translate("en", "movie.productionIntentNote")).toContain(
      "intent fields",
    );
    expect(translate("en", "movie.capabilityClassificationNote")).toContain(
      "Native",
    );
    expect(translate("en", "movie.capabilityClassificationNote")).toContain(
      "Unsupported",
    );
  });

  it("hydrates every advanced control from a server-owned preset", () => {
    const selection = selectionFromPreset(naturalPreset, {
      notes: "Keep the eyeline clear.",
    });
    expect(selection.intent).toBe("natural");
    expect(selection.presetId).toBe("natural-observation");
    expect(selection.focalLength).toBe("28–50mm equivalent");
    expect(selection.notes).toBe("Keep the eyeline clear.");
    expect(selection.capabilityReferences?.[0]?.classification).toBe(
      "Translated",
    );
    expect(presetForIntent([naturalPreset], "NATURAL")?.id).toBe(
      "natural-observation",
    );
  });

  it("keeps custom intent state safe when shot JSON is missing or malformed", () => {
    expect(emptyCinematographySelection("dynamic").intent).toBe("dynamic");
    expect(parseCinematographyJson(null)).toBeNull();
    expect(parseCinematographyJson("not-json")).toBeNull();
    expect(
      parseCinematographyJson(
        JSON.stringify({ intent: "epic", cameraMovement: "orbit" }),
      ),
    ).toEqual({ intent: "epic", cameraMovement: "orbit" });
  });
});
