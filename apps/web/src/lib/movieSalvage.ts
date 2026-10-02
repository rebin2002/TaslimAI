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

function parseMetadata(take: MovieTake, fallbackJson?: string | null): Metadata {
  const json = fallbackJson ?? null;
  if (!json?.trim()) return {};
  try {
    const value: unknown = JSON.parse(json);
    return value && typeof value === "object" && !Array.isArray(value) ? value as Metadata : {};
  } catch {
    return {};
  }
}

function positiveInteger(value: unknown): number | null {
  if (typeof value !== "number" || !Number.isFinite(value) || value <= 0) return null;
  const rounded = Math.round(value);
  return rounded > 0 ? rounded : null;
}

function durationMilliseconds(take: MovieTake, shotDurationSeconds: number | null, fallbackJson?: string | null): number {
  const metadata = parseMetadata(take, fallbackJson);
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

function qcIssues(take: MovieTake, fallbackJson?: string | null): SalvageQcIssue[] {
  const metadata = parseMetadata(take, fallbackJson);
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

function usableRange(take: MovieTake, duration: number, fallbackJson?: string | null): { sourceInMilliseconds: number; sourceOutMilliseconds: number } {
  const metadata = parseMetadata(take, fallbackJson);
  const ranges = Array.isArray(metadata.usableRanges) ? metadata.usableRanges : [];
  const firstRange = ranges.find((item) => item && typeof item === "object") as Record<string, unknown> | undefined;
  const sourceInMilliseconds = positiveInteger(firstRange?.startMilliseconds ?? firstRange?.sourceInMilliseconds ?? metadata.trimInMilliseconds) ?? 0;
  const requestedOut = positiveInteger(firstRange?.endMilliseconds ?? firstRange?.sourceOutMilliseconds ?? metadata.trimOutMilliseconds);
  const sourceOutMilliseconds = Math.min(duration, requestedOut ?? duration);
  return sourceOutMilliseconds > sourceInMilliseconds
    ? { sourceInMilliseconds, sourceOutMilliseconds }
    : { sourceInMilliseconds: 0, sourceOutMilliseconds: duration };
}

export function buildMovieSubclips(project: MovieProject): MovieSubclip[] {
  return project.scenes.flatMap((scene) => scene.shots.flatMap((shot) => shot.takes
    .filter((take) => Boolean(take.assetId) && usableStatuses.has(take.status))
    .map((take) => {
      const clipMetadataJson = shot.clips.find((clip) => clip.id === take.movieClipId)?.metadataJson;
      const duration = durationMilliseconds(take, shot.durationSeconds, clipMetadataJson);
      const range = usableRange(take, duration, clipMetadataJson);
      return {
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
        issues: qcIssues(take, clipMetadataJson),
        selected: Boolean(take.selectedAt),
        finalized: Boolean(take.finalizedAt),
      } satisfies MovieSubclip;
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

export function formatSalvageTime(milliseconds: number): string {
  const safe = Math.max(0, Math.round(milliseconds));
  const minutes = Math.floor(safe / 60_000);
  const seconds = Math.floor((safe % 60_000) / 1000);
  const frames = safe % 1000;
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${String(frames).padStart(3, "0")}`;
}
