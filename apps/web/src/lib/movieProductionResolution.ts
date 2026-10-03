export type MovieResolutionTier = "Draft" | "Upgrade" | "Master";
export type MovieRenderIntent = { targetResolution: "480p" | "720p" | "1080p" | "1440p" | "2160p"; qualityTier: "Fast" | "Standard" | "Cinematic" | "Studio" };

export type MovieResolutionPlanInput = {
  durationSeconds: number;
  shotCount: number;
  selectedTakeCount: number;
  reviewableTakeCount: number;
  failedPassCount: number;
  estimateAvailable?: boolean;
};

export type MovieResolutionTierPlan = {
  id: MovieResolutionTier;
  title: string;
  summary: string;
  detail: string;
  estimate: string;
  estimateDetail: string;
};

export type MovieResolutionPlan = {
  recommended: MovieResolutionTier;
  recommendationReason: string;
  qcLabel: string;
  qcTone: "ready" | "review" | "blocked";
  qcDetail: string;
  escalationLabel: string;
  escalationDetail: string;
  tiers: MovieResolutionTierPlan[];
};

const tierDetails: Record<MovieResolutionTier, Omit<MovieResolutionTierPlan, "estimate" | "estimateDetail">> = {
  Draft: {
    id: "Draft",
    title: "Draft",
    summary: "Check rhythm and framing first.",
    detail: "A lightweight working pass for creative decisions before you commit to a master.",
  },
  Upgrade: {
    id: "Upgrade",
    title: "Upgrade",
    summary: "Make the working cut review-ready.",
    detail: "A balanced pass for continuity, performance, and editorial review.",
  },
  Master: {
    id: "Master",
    title: "Master",
    summary: "Prepare the selected take for delivery.",
    detail: "The highest-finish intent, reserved for a take you are ready to carry forward.",
  },
};

export const movieResolutionTiers: MovieResolutionTier[] = ["Draft", "Upgrade", "Master"];
/** UI intent mapped to the server-side adaptive-resolution contract. */
export function productionIntentForTier(tier: MovieResolutionTier): MovieRenderIntent {
  switch (tier) {
    case "Master": return { targetResolution: "2160p", qualityTier: "Studio" };
    case "Upgrade": return { targetResolution: "1080p", qualityTier: "Cinematic" };
    default: return { targetResolution: "480p", qualityTier: "Fast" };
  }
}

export function buildMovieResolutionPlan(input: MovieResolutionPlanInput): MovieResolutionPlan {
  const estimateAvailable = input.estimateAvailable === true;
  const hasSource = input.reviewableTakeCount > 0 || input.selectedTakeCount > 0;
  const hasSelection = input.selectedTakeCount > 0;
  const hasEscalation = input.failedPassCount > 0;
  const recommended: MovieResolutionTier = hasEscalation ? "Draft" : hasSelection ? "Master" : hasSource ? "Upgrade" : "Draft";
  const recommendationReason = hasEscalation
    ? "Start with a draft pass so the open quality signal can be reviewed before another high-finish pass."
    : hasSelection
      ? "A take is already selected, so the next useful decision is whether it is ready to master."
      : hasSource
        ? "A reviewable source exists, making Upgrade the clearest next step for the working cut."
        : "Start with Draft to validate the source intent before investing in a higher-finish pass.";

  const qcLabel = hasEscalation ? "Needs attention" : hasSource ? "Ready to review" : "Waiting for source";
  const qcTone = hasEscalation ? "blocked" : hasSource ? "ready" : "review";
  const qcDetail = hasEscalation
    ? `${input.failedPassCount} pass${input.failedPassCount === 1 ? "" : "es"} need${input.failedPassCount === 1 ? "s" : ""} another look.`
    : hasSource
      ? "The latest available take can be reviewed without changing the source plan."
      : "Create or attach a real source take before escalating to a higher finish.";
  const escalationLabel = hasEscalation ? "Escalation suggested" : hasSelection ? "No escalation needed" : "Keep it reversible";
  const escalationDetail = hasEscalation
    ? "Review the quality note, then retry only the affected pass."
    : hasSelection
      ? "The selected take is the hand-off point for a master decision."
      : "Planning is safe and does not start rendering on its own.";

  return {
    recommended,
    recommendationReason,
    qcLabel,
    qcTone,
    qcDetail,
    escalationLabel,
    escalationDetail,
    tiers: movieResolutionTiers.map((tier) => ({
      ...tierDetails[tier],
      estimate: estimateAvailable ? estimateFor(tier, input) : "Estimate pending",
      estimateDetail: estimateAvailable ? "Based on this source and finish" : "Available when a render path is connected",
    })),
  };
}

function estimateFor(tier: MovieResolutionTier, input: MovieResolutionPlanInput) {
  const duration = Math.max(input.durationSeconds, 1);
  const shots = Math.max(input.shotCount, 1);
  const multiplier = tier === "Draft" ? 0.6 : tier === "Upgrade" ? 1.2 : 2.4;
  const minutes = Math.max(1, Math.round((duration / 30 + shots / 3) * multiplier));
  return `About ${minutes} min`;
}

export function displayProductionStage(stage: string) {
  switch (stage) {
    case "ShotPlan":
      return "Source plan";
    case "StoryboardCandidate":
    case "ApprovedStoryboard":
      return "Visual plan";
    case "ProductionKeyframe":
    case "ApprovedKeyframe":
      return "Source frame";
    case "MotionPreview":
      return "Motion check";
    case "ProductionRender":
      return "Master pass";
    case "SelectedFinalTake":
      return "Selected take";
    default:
      return stage.replace(/([a-z])([A-Z])/g, "$1 $2");
  }
}

export function displayProductionStatus(status: string) {
  switch (status) {
    case "PendingApproval":
      return "Needs review";
    case "Approved":
      return "Approved";
    case "Rejected":
      return "Revision needed";
    case "Selected":
      return "Selected";
    case "Draft":
      return "Draft";
    default:
      return status.replace(/([a-z])([A-Z])/g, "$1 $2");
  }
}
