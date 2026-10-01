import { expect, type Page } from "@playwright/test";
import { apiJson } from "./fixtures";

export const LAST_SEED_WAVE2_SCENE = {
  title: "The Last Seed — Planting and Rain",
  summary:
    "In a dry village, the young farmer plants his grandfather's last seed, protects it from heat and wind, and sees it begin to grow when rain arrives.",
  durationSeconds: 30,
  continuityNotes:
    "The last seed is the grandfather's gift; the village remains dry until the rain arrives; the farmer and old tree remain consistent.",
};

export const LAST_SEED_WAVE2_SHOTS = [
  {
    key: "gift",
    description:
      "The grandfather places the last seed from the old tree into the young farmer's open palm.",
    purpose: "Establish the emotional promise and the last seed as the story's central object.",
    subjects: "Young farmer; grandfather; last seed",
    locationSet: "Dry village courtyard",
    durationSeconds: 8,
    productionRequirements: "Readable handoff of the single seed; restrained emotional performance.",
    continuityReferences: "Guide: the grandfather's gift and the valley's drought; the seed remains the same object.",
    cameraAndFraming: "Intimate medium close-up on the hands, then both faces in profile",
    cameraMotion: "Slow push-in",
    visualContinuityNotes: "Keep the seed visible, the ochre dust and dry light consistent.",
    cinematography: {
      intent: "intimate",
      shotSize: "medium-close-up",
      focalLength: "50mm",
      lensIntent: "emotional portrait",
      apertureDepthOfField: "shallow focus on the seed",
      cameraAngle: "eye level",
      cameraMovement: "slow push-in",
      frameRateIntent: "24fps",
      lighting: "hard dry afternoon light",
      paletteLook: "ochre and faded green",
      compositionNotes: "The seed bridges the grandfather and farmer in the center of frame.",
    },
  },
  {
    key: "planting",
    description:
      "Despite the warnings, the young farmer presses the last seed into cracked soil and shields it from the wind.",
    purpose: "Make the farmer's irreversible choice and protective action the primary turning point.",
    subjects: "Young farmer; last seed; cracked soil",
    locationSet: "Dry village field",
    durationSeconds: 10,
    productionRequirements: "Visible planting action; dust and wind remain controlled; protect the seed's continuity.",
    continuityReferences: "Follows the grandfather's gift; the dry village and last seed remain grounded in the Guide.",
    cameraAndFraming: "Low medium-wide profile with a close insert of the seed entering the soil",
    cameraMotion: "Gentle lateral track into a locked close-up",
    visualContinuityNotes: "Cracked soil, ochre heat, and the farmer's protective hand must match the preceding shot.",
    cinematography: {
      intent: "natural",
      shotSize: "medium-wide",
      focalLength: "35mm",
      lensIntent: "grounded story action",
      apertureDepthOfField: "deep focus on farmer and soil",
      cameraAngle: "low eye level",
      cameraMovement: "gentle lateral track",
      frameRateIntent: "24fps",
      lighting: "bleached heat",
      paletteLook: "dry ochre with muted green",
      compositionNotes: "The farmer's body forms a shelter around the seed in the lower third.",
    },
  },
  {
    key: "rain",
    description:
      "Rain breaks over the field; the farmer looks up as the protected seed sends a green shoot through the soil.",
    purpose: "Pay off the emotional choice with the first visible sign of hope.",
    subjects: "Young farmer; first green shoot; rain",
    locationSet: "Same dry village field after rain",
    durationSeconds: 12,
    productionRequirements: "Transition from wind and heat to rain; the first green shoot must read clearly without implying a full tree.",
    continuityReferences: "The same planted seed grows after the rain; hope resolves the drought established in the Guide.",
    cameraAndFraming: "Wide field reveal resolving to a close-up of the green shoot",
    cameraMotion: "Tilt from rain-darkened sky down to the shoot",
    visualContinuityNotes: "Preserve the field layout and farmer wardrobe while the palette shifts from ochre to rain greens.",
    cinematography: {
      intent: "natural",
      shotSize: "wide-to-close",
      focalLength: "28mm to 65mm",
      lensIntent: "reveal and emotional detail",
      apertureDepthOfField: "deep reveal, shallow shoot detail",
      cameraAngle: "slightly low",
      cameraMovement: "slow tilt down",
      frameRateIntent: "24fps",
      lighting: "soft overcast rain light",
      paletteLook: "rain green against softened ochre",
      compositionNotes: "Leave negative space above the farmer before revealing the shoot at frame bottom.",
    },
  },
] as const;

export type LastSeedWave2Shot = (typeof LAST_SEED_WAVE2_SHOTS)[number];

export type Wave2ShotContract = {
  schemaVersion?: number;
  durationSeconds?: number | null;
  narrativeImportance?: string | null;
  productionComplexity?: { level?: string | null; drivers?: string[] | null; notes?: string | null } | null;
  qualityRequirements?: { minimumLevel?: string | null; acceptanceCriteria?: string[] | null; notes?: string | null } | null;
  continuitySensitivity?: string | null;
  upscaleSuitability?: string | null;
  targetOutputRequirements?: { aspectRatio?: string | null; resolutionIntent?: string | null } | null;
  planningStatus?: string;
  productionStage?: string;
};

export type LastSeedWave2ShotResponse = {
  id: string;
  sequence: number;
  description: string;
  purpose: string | null;
  subjects: string | null;
  locationSet: string | null;
  durationSeconds: number | null;
  productionRequirements: string | null;
  continuityReferences: string | null;
  cameraAndFraming: string | null;
  cameraMotion: string | null;
  visualContinuityNotes: string | null;
  status: string;
  planState: string;
  readiness: { ready: boolean; missing: string[] };
  productionStage: string;
  clips: Array<{ assetId: string | null }>;
  productionVersions: Array<{
    id: string;
    stage: string;
    status: string;
    assetId: string | null;
    generationJobId: string | null;
    continuitySnapshotReferenceJson?: string | null;
  }>;
  productionContract?: Wave2ShotContract | null;
};

export type LastSeedWave2Plan = {
  scene: {
    id: string;
    sequence: number;
    title: string;
    summary: string;
    durationSeconds: number | null;
    continuityNotes: string | null;
  };
  shots: LastSeedWave2ShotResponse[];
  selectedShot: LastSeedWave2ShotResponse;
  approvedStoryboardId: string;
  integrationRequirements: string[];
};

/**
 * Seeds only persistence-level planning records. It deliberately does not call a
 * generation endpoint or pass provider/model/cost data. When the later Wave 2
 * shot contract is integrated, the optional productionContract assertions below
 * become active without changing the fixture's narrative data.
 */
export async function seedLastSeedWave2Plan(
  page: Page,
  movieProjectId: string,
  prepareForProduction?: (sceneId: string, selectedShotId: string) => Promise<void>,
): Promise<LastSeedWave2Plan> {
  const scene = await apiJson<LastSeedWave2Plan["scene"]>(
    page.request,
    "POST",
    `/api/movie-studio/projects/${movieProjectId}/scenes`,
    LAST_SEED_WAVE2_SCENE,
  );

  const shots: LastSeedWave2ShotResponse[] = [];
  for (const shotFixture of LAST_SEED_WAVE2_SHOTS) {
    shots.push(
      await apiJson<LastSeedWave2ShotResponse>(
        page.request,
        "POST",
        `/api/movie-studio/scenes/${scene.id}/shots`,
        shotFixture,
      ),
    );
  }

  const plan = await apiJson<{
    sceneId: string;
    sceneSequence: number;
    sceneTitle: string;
    sceneSummary: string;
    sceneDurationSeconds: number | null;
    shots: LastSeedWave2ShotResponse[];
    totalDurationSeconds: number;
  }>(page.request, "GET", `/api/movie-studio/scenes/${scene.id}/shots`);

  expect(plan.sceneId).toBe(scene.id);
  expect(plan.sceneDurationSeconds).toBe(LAST_SEED_WAVE2_SCENE.durationSeconds);
  expect(plan.totalDurationSeconds).toBe(LAST_SEED_WAVE2_SCENE.durationSeconds);
  expect(plan.shots).toHaveLength(LAST_SEED_WAVE2_SHOTS.length);
  expect(plan.shots.every((shot) => (shot.durationSeconds ?? 0) > 0 && (shot.durationSeconds ?? 0) <= 30)).toBeTruthy();
  expect(plan.shots.map((shot) => shot.durationSeconds)).toEqual([8, 10, 12]);

  const selectedShot = plan.shots.find((shot) => shot.sequence === 2);
  expect(selectedShot).toBeTruthy();
  if (!selectedShot) throw new Error("The deterministic Last Seed turning-point shot was not returned.");
  await prepareForProduction?.(scene.id, selectedShot.id);

  const storyboardCandidate = await apiJson<{ id: string; stage: string; status: string }>(
    page.request,
    "POST",
    `/api/movie-studio/shots/${selectedShot.id}/production/versions`,
    {
      stage: "StoryboardCandidate",
      label: "Last Seed deterministic storyboard candidate",
      compositionJson: JSON.stringify({ framing: "farmer shelters planted seed", fixture: "last-seed-wave2" }),
      stageProvenanceJson: JSON.stringify({ source: "deterministic-last-seed-wave2-fixture" }),
    },
  );
  expect(storyboardCandidate.stage).toBe("StoryboardCandidate");
  expect(storyboardCandidate.status).toBe("PendingApproval");

  const approvedStoryboard = await apiJson<{
    id: string;
    stage: string;
    status: string;
    assetId: string | null;
    generationJobId: string | null;
    continuitySnapshotReferenceJson?: string | null;
  }>(
    page.request,
    "POST",
    `/api/movie-studio/production/versions/${storyboardCandidate.id}/review`,
    { approve: true, reason: "Deterministic Last Seed storyboard is grounded and reviewable." },
  );
  expect(approvedStoryboard.stage).toBe("ApprovedStoryboard");
  expect(approvedStoryboard.status).toBe("Approved");
  expect(approvedStoryboard.assetId).toBeNull();
  expect(approvedStoryboard.generationJobId).toBeNull();
  expect(approvedStoryboard.continuitySnapshotReferenceJson).toBeTruthy();

  const selectedShotAfterReview = await apiJson<LastSeedWave2ShotResponse>(
    page.request,
    "GET",
    `/api/movie-studio/shots/${selectedShot.id}`,
  );
  const integrationRequirements = validateOptionalWave2ShotContract(selectedShotAfterReview);

  return {
    scene,
    shots: plan.shots,
    selectedShot: selectedShotAfterReview,
    approvedStoryboardId: approvedStoryboard.id,
    integrationRequirements,
  };
}

/**
 * The base commit predates the optional Wave 2 production shot contract. Keep
 * this validation in one seam so a future integration can expose the contract
 * without replacing the Last Seed fixture or asserting creative prose.
 */
export function validateOptionalWave2ShotContract(shot: LastSeedWave2ShotResponse): string[] {
  const contract = shot.productionContract;
  if (!contract) {
    return [
      "Expose MovieShot.productionContract (or an equivalent stable Wave 2 contract projection) for narrative importance, production complexity, quality, continuity sensitivity, and target output metadata.",
    ];
  }

  const requirements: string[] = [];
  expect(contract.durationSeconds).toBe(shot.durationSeconds);
  expect(["background", "supporting", "primary", "critical"]).toContain(contract.narrativeImportance);
  expect(["low", "moderate", "high", "critical"]).toContain(contract.productionComplexity?.level);
  expect(["Fast", "Standard", "Cinematic", "Studio"]).toContain(contract.qualityRequirements?.minimumLevel);
  expect(["low", "moderate", "high", "locked"]).toContain(contract.continuitySensitivity);
  expect(["preferred", "conditional", "not_recommended"]).toContain(contract.upscaleSuitability);
  expect(contract.targetOutputRequirements?.aspectRatio).toBe("16:9");
  expect(contract.planningStatus).toBeTruthy();
  expect(contract.productionStage).toBeTruthy();
  return requirements;
}
