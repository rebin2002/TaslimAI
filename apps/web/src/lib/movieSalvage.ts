import type {
  MovieProject,
  MovieTake,
  MovieTimeline,
  MovieTimelineRevisionRequest,
} from "./api";

export type SalvageQcSeverity = "error" | "warning" | "info";
export type SalvageDecision = "pending" | "accepted" | "rejected";

export type SalvageQcIssue = {
  code: string;
  label: string;
  severity: SalvageQcSeverity;
  detail: string;
};

export type MovieSubclip = {
  id: string;
  takeId: string;
  shotId: string;
  sceneId: string;
  sceneSequence: number;
  sceneTitle: string;
  shotSequence: number;
  shotDescription: string;
  label: string;
  status: string;
  assetId: string;
  durationMilliseconds: number;
  sourceInMilliseconds: number;
  sourceOutMilliseconds: number;
  issues: SalvageQcIssue[];
  selected: boolean;
  finalized: boolean;
};

export type SalvageRecommendation = {
  id: string;
  subclip: MovieSubclip;
  title: string;
  summary: string;
  rationale: string[];
  decision: SalvageDecision;
};

export type SalvageInsertProposal = {
  recommendationId: string;
  takeId: string;
  sourceInMilliseconds: number;
  sourceOutMilliseconds: number;
  timelineInMilliseconds: number;
  label: string;
};

type Metadata = Record<string, unknown>;
const usableStatuses = new Set(["Succeeded", "Ready", "Approved", "Selected", "ReviewRequired"]);

function parseMetadata(json?: string | null): Metadata | null {
  if (!json?.trim()) return {};
  try {
    const value: unknown = JSON.parse(json);
    return value && typeof value === "object" && !Array.isArray(value) ? value as Metadata : null;
  } catch {
    return null;
  }
}

function positiveInteger(value: unknown): number | null {
  if (typeof value !== "number" || !Number.isFinite(value) || value <= 0) return null;
  const rounded = Math.round(value);
  return rounded > 0 ? rounded : null;
}

function durationMilliseconds(metadata: Metadata, shotDurationSeconds: number | null): number {
  return positiveInteger(metadata.durationMilliseconds)
    ?? positiveInteger(metadata.durationMs)
    ?? (typeof metadata.durationSeconds === "number" ? positiveInteger(metadata.durationSeconds * 1000) : null)
    ?? (shotDurationSeconds && shotDurationSeconds > 0 ? shotDurationSeconds * 1000 : 0);
}

function issueText(value: unknown): Array<{ label: string; detail: string; severity: SalvageQcSeverity }> {
  if (!Array.isArray(value)) return [];
  return value.flatMap((item) => {
    if (typeof item === "string" && item.trim()) return [{ label: item.trim(), detail: item.trim(), severity: "warning" as const }];
    if (!item || typeof item !== "object") return [];
    const record = item as Record<string, unknown>;
    const detail = typeof record.detail === "string" ? record.detail : typeof record.message === "string" ? record.message : typeof record.label === "string" ? record.label : "QC evidence needs review.";
    const label = typeof record.label === "string" ? record.label : detail;
    const severity = record.severity === "error" || record.severity === "info" ? record.severity : "warning";
    return [{ label, detail, severity }];
  });
}

function qcIssues(take: MovieTake, metadata: Metadata): SalvageQcIssue[] {
  const qualityControl = metadata.qualityControl && typeof metadata.qualityControl === "object" ? metadata.qualityControl as Metadata : {};
  const rawIssues = [
    ...issueText(metadata.qcIssues),
    ...issueText(metadata.issues),
    ...issueText(qualityControl.issues),
    ...issueText(metadata.continuityWarnings),
  ];
  const issues = rawIssues.map((issue, index) => ({
    code: `MOVIE_SALVAGE_QC_${index + 1}`,
    label: issue.label,
    severity: issue.severity,
    detail: issue.detail,
  }));
  if (take.status === "ReviewRequired") {
    issues.unshift({ code: "MOVIE_SALVAGE_REVIEW_REQUIRED", label: "Review required", severity: "warning", detail: "This take was retained, but an upstream change marked it for human review." });
  }
  if (take.execution?.status === "Failed") {
    issues.unshift({ code: "MOVIE_SALVAGE_EXECUTION_FAILED", label: "Execution failed", severity: "error", detail: "The recorded pass failed and cannot be placed on the timeline." });
  }
  return issues;
}

function usableRange(metadata: Metadata, duration: number): Pick<MovieSubclip, "sourceInMilliseconds" | "sourceOutMilliseconds"> | null {
  let start = metadata.trimInMilliseconds;
  let end = metadata.trimOutMilliseconds;
  if ("usableRanges" in metadata) {
    // A persisted empty list means QC found no usable footage. Never turn an
    // unreadable first selection into a full-take insertion.
    const first: unknown = Array.isArray(metadata.usableRanges) ? metadata.usableRanges[0] : null;
    if (!first || typeof first !== "object" || Array.isArray(first)) return null;
    const range = first as Metadata;
    if (!("startMilliseconds" in range || "sourceInMilliseconds" in range ||
          "endMilliseconds" in range || "sourceOutMilliseconds" in range)) return null;
    if ("startMilliseconds" in range) start = range.startMilliseconds;
    else if ("sourceInMilliseconds" in range) start = range.sourceInMilliseconds;
    if ("endMilliseconds" in range) end = range.endMilliseconds;
    else if ("sourceOutMilliseconds" in range) end = range.sourceOutMilliseconds;
  }
  if (start !== undefined && (typeof start !== "number" || !Number.isFinite(start) || start < 0)) return null;
  if (end !== undefined && (typeof end !== "number" || !Number.isFinite(end) || end < 0)) return null;
  const sourceInMilliseconds = start === undefined ? 0 : Math.round(start);
  const sourceOutMilliseconds = end === undefined ? duration : Math.min(duration, Math.round(end));
  return sourceOutMilliseconds > sourceInMilliseconds ? { sourceInMilliseconds, sourceOutMilliseconds } : null;
}

export function buildMovieSubclips(project: MovieProject): MovieSubclip[] {
  return project.scenes.flatMap((scene) => scene.shots.flatMap((shot) => shot.takes
    .filter((take) => Boolean(take.assetId) && usableStatuses.has(take.status))
    .flatMap((take) => {
      const metadata = parseMetadata(shot.clips.find((clip) => clip.id === take.movieClipId)?.metadataJson);
      if (!metadata) return [];
      const duration = durationMilliseconds(metadata, shot.durationSeconds);
      const range = usableRange(metadata, duration);
      if (!range) return [];
      return [{
        id: `subclip-${take.id}`,
        takeId: take.id,
        shotId: shot.id,
        sceneId: scene.id,
        sceneSequence: scene.sequence,
        sceneTitle: scene.title,
        shotSequence: shot.sequence,
        shotDescription: shot.description,
        label: take.label || `Take v${take.versionNumber}`,
        status: take.status,
        assetId: take.assetId!,
        durationMilliseconds: duration,
        sourceInMilliseconds: range.sourceInMilliseconds,
        sourceOutMilliseconds: range.sourceOutMilliseconds,
        issues: qcIssues(take, metadata),
        selected: Boolean(take.selectedAt),
        finalized: Boolean(take.finalizedAt),
      } satisfies MovieSubclip];
    })));
}

export function buildSalvageRecommendations(project: MovieProject): SalvageRecommendation[] {
  return buildMovieSubclips(project).filter((subclip) => subclip.durationMilliseconds > 0).map((subclip) => {
    const issueCount = subclip.issues.length;
    const rangeDuration = subclip.sourceOutMilliseconds - subclip.sourceInMilliseconds;
    return {
      id: `salvage-${subclip.takeId}`,
      subclip,
      title: `Salvage ${subclip.label}`,
      summary: issueCount ? "Keep the usable range visible and carry the QC note into review instead of regenerating the whole shot." : "This take has a bounded usable range. Review it as raw footage before spending on another pass.",
      rationale: [
        `${Math.round(rangeDuration / 1000)}s usable range from a ${Math.round(subclip.durationMilliseconds / 1000)}s take`,
        issueCount ? `${issueCount} QC issue${issueCount === 1 ? "" : "s"} attached to the recommendation` : "No persisted QC issue was recorded for this take",
        subclip.selected ? "The shot already points at this take" : "The take remains separate until a human selects it",
      ],
      decision: "pending",
    } satisfies SalvageRecommendation;
  });
}

export function buildSalvageTimelineRevision(
  timeline: MovieTimeline | null,
  proposal: SalvageInsertProposal,
  recommendation: SalvageRecommendation,
  movieTakeSelectId?: string,
): MovieTimelineRevisionRequest {
  const current = timeline?.currentRevision;
  const tracks = (current?.tracks ?? []).map((track) => ({
    kind: track.kind,
    name: track.name,
    trackNumber: track.trackNumber,
    isMuted: track.isMuted,
    items: track.items.map((item) => ({
      kind: item.kind,
      sourceTakeId: item.sourceTakeId,
      sourceAssetId: item.sourceAssetId,
      timelineInMilliseconds: item.timelineInMilliseconds,
      timelineOutMilliseconds: item.timelineOutMilliseconds,
      sourceInMilliseconds: item.sourceInMilliseconds,
      sourceOutMilliseconds: item.sourceOutMilliseconds,
      label: item.label,
      metadataJson: item.metadataJson,
    })),
  }));
  let videoTrack = tracks.find((track) => track.kind === "Video");
  if (!videoTrack) {
    videoTrack = { kind: "Video", name: "Selects", trackNumber: Math.max(0, ...tracks.map((track) => track.trackNumber)) + 1, isMuted: false, items: [] };
    tracks.push(videoTrack);
  }
  videoTrack.items.push({
    kind: "VisualTake",
    sourceTakeId: proposal.takeId,
    sourceAssetId: null,
    timelineInMilliseconds: proposal.timelineInMilliseconds,
    timelineOutMilliseconds: proposal.timelineInMilliseconds + (proposal.sourceOutMilliseconds - proposal.sourceInMilliseconds),
    sourceInMilliseconds: proposal.sourceInMilliseconds,
    sourceOutMilliseconds: proposal.sourceOutMilliseconds,
    label: proposal.label,
    metadataJson: JSON.stringify({
      source: "salvage-director",
      recommendationId: recommendation.id,
      movieTakeSelectId: movieTakeSelectId ?? null,
      qcIssueCodes: recommendation.subclip.issues.map((issue) => issue.code),
    }),
  });
  return {
    baseRevisionId: current?.id ?? null,
    label: "Selects salvage insert",
    changeSummary: `Accepted ${recommendation.title} as a minimal timeline insert.`,
    tracks,
  };
}

const HOUR_MILLISECONDS = 3_600_000;

/**
 * The movie timeline's shared wall-clock timecode (#685 client lane).
 *
 * Below one hour the output stays byte-identical to the long-standing
 * per-clip readout - `MM:SS.mmm`, or `MM:SS` with `showMilliseconds: false` -
 * because that is what customers already read for a single provider-capped
 * clip and source range. A whole-film plan may legitimately total up to
 * 120 minutes while each clip stays inside its own short cap, so at or above
 * one hour the timecode rolls into `HH:MM:SS(.mmm)` instead of letting the
 * minutes field run past 59. Presentation scale only: this changes no
 * duration bound, provider cap, or billing value.
 */
export function formatMovieTimecode(
  milliseconds: number,
  options: { showMilliseconds?: boolean } = {},
): string {
  const safe = Math.max(0, Math.round(milliseconds));
  const hours = Math.floor(safe / HOUR_MILLISECONDS);
  const minutes = Math.floor((safe % HOUR_MILLISECONDS) / 60_000);
  const seconds = Math.floor((safe % 60_000) / 1000);
  const frames = safe % 1000;
  const clock = hours > 0
    ? `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
  return options.showMilliseconds === false ? clock : `${clock}.${String(frames).padStart(3, "0")}`;
}

export function formatSalvageTime(milliseconds: number): string {
  return formatMovieTimecode(milliseconds);
}
