import type { Page } from "@playwright/test";
import { apiJson } from "./fixtures";

export type MovieBrowserOperationalFixture = {
  movieProjectId: string;
  actId: string;
  sequenceId: string;
  sceneId: string;
  shotId: string;
  characterId: string;
  locationId: string;
  setId: string;
  propId: string;
  storyRevisionId: string;
  storyboardCandidateId: string;
  keyframeId: string;
  takeId: string;
};

type IdResponse = { id: string };
type StoryResponse = { currentRevisionId: string | null };
type ProductionResponse = {
  id: string;
  stage: string;
  status: string;
  assetId: string | null;
  generationJobId: string | null;
};
type MovieProjectResponse = {
  id: string;
  mode: "Quick" | "Full";
  scenes: Array<{
    id: string;
    title: string;
    shots: Array<{
      id: string;
      productionVersions: ProductionResponse[];
      clips: Array<{ assetId: string | null }>;
    }>;
    clips: Array<{ assetId: string | null }>;
  }>;
  characters: Array<{ id: string; name: string }>;
  locations: Array<{ id: string; name: string }>;
  clips: Array<{ assetId: string | null }>;
};

export type MovieBrowserDiagnostic = {
  url: string;
  status: number;
  method: string;
};

export function startMovieDiagnostics(page: Page) {
  const events: MovieBrowserDiagnostic[] = [];
  const listener = (response: {
    url(): string;
    status(): number;
    request(): { method(): string };
  }) => {
    if (response.url().includes("/api/")) {
      events.push({
        url: response.url(),
        status: response.status(),
        method: response.request().method(),
      });
    }
  };
  page.on("response", listener);
  return {
    events,
    stop() {
      page.off("response", listener);
      return events.slice();
    },
  };
}

export async function seedMovieOperationalRecords(
  page: Page,
  movieProjectId: string,
): Promise<MovieBrowserOperationalFixture> {
  const act = await apiJson<IdResponse>(
    page.request,
    "POST",
    `/api/movie-studio/projects/${movieProjectId}/acts`,
    {
      title: "Act One",
      summary: "The first irreversible choice.",
    },
  );
  const sequence = await apiJson<IdResponse>(
    page.request,
    "POST",
    `/api/movie-studio/acts/${act.id}/sequences`,
    {
      title: "The Arrival",
      summary: "The courier reaches the harbor.",
    },
  );
  const scene = await apiJson<IdResponse>(
    page.request,
    "POST",
    `/api/movie-studio/sequences/${sequence.id}/scenes`,
    {
      title: "Blue Hour Harbor",
      summary: "The courier enters the rain-soaked warehouse.",
    },
  );
  const shot = await apiJson<IdResponse>(
    page.request,
    "POST",
    `/api/movie-studio/scenes/${scene.id}/shots`,
    {
      description: "A slow push toward the compass on a crate.",
      cameraAndFraming: "24mm wide, subject left",
      cameraMotion: "slow push",
      durationSeconds: 5,
      visualContinuityNotes: "Keep the red practical in frame.",
      cinematography: {
        intent: "natural",
        shotSize: "wide",
        focalLength: "24mm",
        lensIntent: "environmental",
        apertureDepthOfField: "deep focus",
        cameraAngle: "eye level",
        cameraMovement: "slow push",
        frameRateIntent: "24fps",
        lighting: "warm practicals",
        paletteLook: "teal and amber",
        compositionNotes: "Subject left, leading lines to the compass.",
      },
    },
  );
  const story = await apiJson<StoryResponse>(
    page.request,
    "POST",
    `/api/movie-studio/projects/${movieProjectId}/story/revisions`,
    {
      premise: "A courier must deliver a message before dawn.",
      logline:
        "When the city goes dark, a reluctant courier crosses a hostile district to deliver one message.",
      synopsis:
        "The courier discovers that the message is connected to a missing sibling.",
      treatment: "The route becomes a moral test and ends in a public choice.",
      authorship: "Human",
      changeSummary: "Operational browser story pass",
      scenes: [
        {
          sceneIdentifier: "ACT-1-SEQUENCE-1-SCENE-1",
          actNumber: 1,
          sequenceNumber: 1,
          movieSceneId: scene.id,
          slugline: "EXT. HARBOR WAREHOUSE - BLUE HOUR",
          synopsis: "The courier arrives as the lights fail.",
          elements: [
            {
              elementType: "Action",
              content: "The last streetlamp flickers out.",
            },
            {
              elementType: "Dialogue",
              content: "We have one hour.",
              characterName: "MARA",
              parenthetical: "quietly",
            },
          ],
        },
      ],
    },
  );
  if (!story.currentRevisionId)
    throw new Error(
      "The deterministic story fixture did not return a revision id.",
    );
  await apiJson(
    page.request,
    "POST",
    `/api/movie-studio/projects/${movieProjectId}/story/revisions/${story.currentRevisionId}/submit`,
  );
  await apiJson(
    page.request,
    "POST",
    `/api/movie-studio/projects/${movieProjectId}/story/revisions/${story.currentRevisionId}/approve`,
  );

  const character = await apiJson<IdResponse>(
    page.request,
    "POST",
    `/api/movie-studio/projects/${movieProjectId}/characters`,
    {
      name: "Mara",
      role: "Courier",
      description: "A guarded courier who refuses to abandon the message.",
      appearance: "Short dark hair and a weathered navy coat.",
      wardrobe: "Navy coat, brass compass, worn boots.",
      voiceAndPerformance: "Quiet, deliberate, increasingly urgent.",
      continuityNotes: "Compass stays in the left hand.",
    },
  );
  const location = await apiJson<IdResponse>(
    page.request,
    "POST",
    `/api/movie-studio/projects/${movieProjectId}/locations`,
    {
      name: "Old Harbor",
      description: "A repeatable waterfront location under repair.",
      visualContinuityNotes: "Rust-red cranes remain on the east horizon.",
    },
  );
  const set = await apiJson<IdResponse>(
    page.request,
    "POST",
    `/api/movie-studio/projects/${movieProjectId}/sets`,
    {
      name: "Harbor Warehouse",
      description: "A practical warehouse interior.",
      environmentType: "practical",
      movieLocationId: location.id,
      visualDescription: "Wet concrete and sodium spill.",
      timeOfDay: "blue hour",
      weather: "light rain",
    },
  );
  const prop = await apiJson<IdResponse>(
    page.request,
    "POST",
    `/api/movie-studio/projects/${movieProjectId}/props`,
    {
      name: "Brass Compass",
      description: "A worn brass compass with a cracked glass face.",
      category: "hero prop",
      continuityNotes: "The crack faces camera in close shots.",
    },
  );
  await apiJson(
    page.request,
    "POST",
    `/api/movie-studio/scenes/${scene.id}/world-usage`,
    {
      entityType: "set",
      entityId: set.id,
      role: "primary environment",
    },
  );
  await apiJson(
    page.request,
    "POST",
    `/api/movie-studio/scenes/${scene.id}/world-usage`,
    {
      entityType: "prop",
      entityId: prop.id,
      movieShotId: shot.id,
      role: "hero prop",
    },
  );

  const storyboardCandidate = await apiJson<ProductionResponse>(
    page.request,
    "POST",
    `/api/movie-studio/shots/${shot.id}/production/versions`,
    {
      stage: "StoryboardCandidate",
      label: "Deterministic storyboard candidate",
      compositionJson: JSON.stringify({ blocking: "subject-left", seed: 42 }),
      stageProvenanceJson: JSON.stringify({
        source: "deterministic-browser-path",
      }),
    },
  );
  const approvedStoryboard = await apiJson<ProductionResponse>(
    page.request,
    "POST",
    `/api/movie-studio/production/versions/${storyboardCandidate.id}/review`,
    {
      approve: true,
      reason: "Storyboard composition is approved.",
    },
  );
  const keyframe = await apiJson<ProductionResponse>(
    page.request,
    "POST",
    `/api/movie-studio/shots/${shot.id}/production/versions`,
    {
      stage: "ProductionKeyframe",
      sourceVersionId: storyboardCandidate.id,
      compositionJson: JSON.stringify({ frame: "approved-keyframe", seed: 42 }),
      stageProvenanceJson: JSON.stringify({ source: "approved-storyboard" }),
    },
  );
  await apiJson<ProductionResponse>(
    page.request,
    "POST",
    `/api/movie-studio/production/versions/${keyframe.id}/review`,
    {
      approve: true,
      reason: "Keyframe continuity is approved.",
    },
  );
  if (
    approvedStoryboard.stage !== "ApprovedStoryboard" ||
    approvedStoryboard.assetId ||
    approvedStoryboard.generationJobId
  ) {
    throw new Error(
      "Storyboard candidate did not remain a persistence-only, approved record.",
    );
  }

  const take = await apiJson<IdResponse>(
    page.request,
    "POST",
    `/api/movie-studio/shots/${shot.id}/takes`,
    {
      label: "Deterministic production take",
      qualityLevel: "Standard",
      autoDirectorEnabled: false,
      notes: "Persistence-only take fixture; no generated output is implied.",
    },
  );
  await apiJson(
    page.request,
    "POST",
    `/api/movie-studio/takes/${take.id}/approvals`,
    { decision: "Approved", comment: "Take is reviewable." },
  );
  await apiJson(
    page.request,
    "POST",
    `/api/movie-studio/takes/${take.id}/select`,
  );
  await apiJson(
    page.request,
    "POST",
    `/api/movie-studio/takes/${take.id}/finalize`,
  );

  return {
    movieProjectId,
    actId: act.id,
    sequenceId: sequence.id,
    sceneId: scene.id,
    shotId: shot.id,
    characterId: character.id,
    locationId: location.id,
    setId: set.id,
    propId: prop.id,
    storyRevisionId: story.currentRevisionId,
    storyboardCandidateId: storyboardCandidate.id,
    keyframeId: keyframe.id,
    takeId: take.id,
  };
}

export async function getMovieProject(page: Page, movieProjectId: string) {
  return apiJson<MovieProjectResponse>(
    page.request,
    "GET",
    `/api/movie-studio/projects/${movieProjectId}`,
  );
}
