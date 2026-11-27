/**
 * Issue #1259 (Academic Research Studio) — Phase A capability registry.
 *
 * The academic surface is deliberately wider than the backend that currently
 * supports it. This module is the single, honest source of truth for which
 * academic affordances are real today, so a component can never render a
 * control that looks functional but silently does nothing.
 *
 * Contract every consumer relies on:
 * - `available`   the surface ships the behaviour deterministically, including
 *                 local controls and the authenticated persisted Plan checkpoint.
 * - `planned`     the behaviour needs backend work from a later phase. It may
 *                 only be rendered as a labelled status, never as a button.
 * - `unavailable` the behaviour is explicitly out of scope for academic
 *                 research and must be explained rather than offered.
 *
 * Capability status and workflow-stage runnability are deliberately separate
 * judgements: a stage only shows as available when its own execution surface
 * exists. The Plan API is now connected; a standalone, read-only scholarly
 * search still does not make the separate Discover workflow stage runnable.
 *
 * Vendor, provider and model names must never reach this file: customer-facing
 * copy describes capabilities by function only (issue #1259, "integrity,
 * privacy and safety"). Cost figures are likewise internal and never surfaced.
 */

/** Lifecycle phase from the issue's delivery plan that a capability belongs to. */
export type AcademicCapabilityPhase = "A" | "B" | "C" | "D";

/** Honesty states a rendered academic affordance can be in. */
export type AcademicCapabilityStatus = "available" | "planned" | "unavailable";

export type AcademicCapabilityId =
  | "briefSetup"
  | "citationFormatting"
  | "identifierValidation"
  | "sourceReview"
  | "standardResearch"
  | "localDraftPersistence"
  | "manualEvidenceNotes"
  | "chapterPlanning"
  | "persistedPlan"
  | "scholarlyDiscovery"
  | "isbnCatalogLookup"
  | "fullTextRetrieval"
  | "automatedScreening"
  | "evidenceExtraction"
  | "contradictionDetection"
  | "gapAnalysis"
  | "academicJob"
  | "resumeCheckpoints"
  | "budgetPreflight"
  | "chapterDrafting"
  | "documentExport";

export type AcademicCapability = {
  id: AcademicCapabilityId;
  status: AcademicCapabilityStatus;
  /** Delivery phase the capability lands in (`available` is Phase A, this slice). */
  phase: AcademicCapabilityPhase;
  /** i18n key for the one-line customer-facing description. */
  detailKey: string;
};

/**
 * The registry. `available` entries are implemented by this slice; everything
 * else is a status label only. Adding a planned capability here is the only
 * supported way to mention a future behaviour in the UI.
 */
export const academicCapabilities: Readonly<Record<AcademicCapabilityId, AcademicCapability>> = {
  briefSetup: { id: "briefSetup", status: "available", phase: "A", detailKey: "capability.available.brief" },
  identifierValidation: { id: "identifierValidation", status: "available", phase: "A", detailKey: "capability.available.identifiers" },
  citationFormatting: { id: "citationFormatting", status: "available", phase: "A", detailKey: "capability.available.citations" },
  sourceReview: { id: "sourceReview", status: "available", phase: "A", detailKey: "capability.available.sourceReview" },
  standardResearch: { id: "standardResearch", status: "available", phase: "A", detailKey: "capability.available.standard" },
  localDraftPersistence: { id: "localDraftPersistence", status: "available", phase: "A", detailKey: "capability.available.draft" },
  manualEvidenceNotes: { id: "manualEvidenceNotes", status: "available", phase: "A", detailKey: "capability.available.evidenceManual" },
  chapterPlanning: { id: "chapterPlanning", status: "available", phase: "A", detailKey: "capability.available.chapterPlanning" },
  persistedPlan: { id: "persistedPlan", status: "available", phase: "C", detailKey: "capability.available.persistedPlan" },
  // #1267 slice 1: the workspace-scoped, read-only search endpoint is live, so discovering
  // bibliographic records and resolving the identifiers inside them is real behaviour, rendered
  // as a working form. Everything that needs the content itself stays planned: an indicated
  // full-text location is not the same affordance as reading it, and the panel says so.
  scholarlyDiscovery: { id: "scholarlyDiscovery", status: "available", phase: "A", detailKey: "capability.available.discovery" },
  isbnCatalogLookup: { id: "isbnCatalogLookup", status: "available", phase: "A", detailKey: "capability.available.isbnLookup" },
  fullTextRetrieval: { id: "fullTextRetrieval", status: "planned", phase: "B", detailKey: "capability.planned.fullText" },
  automatedScreening: { id: "automatedScreening", status: "planned", phase: "B", detailKey: "capability.planned.screening" },
  evidenceExtraction: { id: "evidenceExtraction", status: "planned", phase: "B", detailKey: "capability.planned.extraction" },
  contradictionDetection: { id: "contradictionDetection", status: "planned", phase: "B", detailKey: "capability.planned.contradictions" },
  gapAnalysis: { id: "gapAnalysis", status: "planned", phase: "B", detailKey: "capability.planned.gapAnalysis" },
  academicJob: { id: "academicJob", status: "planned", phase: "C", detailKey: "capability.planned.job" },
  resumeCheckpoints: { id: "resumeCheckpoints", status: "planned", phase: "C", detailKey: "capability.planned.resume" },
  budgetPreflight: { id: "budgetPreflight", status: "planned", phase: "C", detailKey: "capability.planned.budget" },
  chapterDrafting: { id: "chapterDrafting", status: "planned", phase: "C", detailKey: "capability.planned.drafting" },
  documentExport: { id: "documentExport", status: "planned", phase: "C", detailKey: "capability.planned.export" },
};

export const academicCapabilityIds = Object.keys(academicCapabilities) as AcademicCapabilityId[];

/** Guarded lookup: an unknown id resolves to `unavailable`, never to a guess. */
export function capabilityOf(id: AcademicCapabilityId | string): AcademicCapability {
  const known = academicCapabilities[id as AcademicCapabilityId];
  if (known) return known;
  // Unknown ids must fail closed so a stale call site can never imply a
  // feature exists.
  return { id: "academicJob", status: "unavailable", phase: "A", detailKey: "capability.unknown" };
}

export function isCapabilityAvailable(id: AcademicCapabilityId): boolean {
  return academicCapabilities[id].status === "available";
}

/** True when the behaviour is a future phase and may only be shown as a status. */
export function isCapabilityPlanned(id: AcademicCapabilityId): boolean {
  return academicCapabilities[id].status === "planned";
}

/**
 * The staged academic workflow from the issue, in order. Each stage carries the
 * capability that would make it real, so the Plan tab can state precisely
 * which stages run on this slice and which are pending backend work.
 */
/**
 * What executes a workflow stage: a local UI surface, the persisted Plan API,
 * or a downstream job stage that is not connected in this slice.
 */
export type AcademicWorkflowStageExecutor = "ui" | "planApi" | "job";

export type AcademicWorkflowStage = {
  id: string;
  capability: AcademicCapabilityId;
  executor: AcademicWorkflowStageExecutor;
  labelKey: string;
  descriptionKey: string;
};

export const academicWorkflowStages: readonly AcademicWorkflowStage[] = [
  { id: "plan", capability: "persistedPlan", executor: "planApi", labelKey: "stage.plan.label", descriptionKey: "stage.plan.detail" },
  // The discovery capability is real today (standalone read-only search in the
  // Sources tab), but this entry describes the discovery *stage* of the staged
  // job. No job runner is wired, so the stage stays non-runnable by executor.
  { id: "discovery", capability: "scholarlyDiscovery", executor: "job", labelKey: "stage.discovery.label", descriptionKey: "stage.discovery.detail" },
  { id: "screen", capability: "automatedScreening", executor: "job", labelKey: "stage.screen.label", descriptionKey: "stage.screen.detail" },
  { id: "extract", capability: "evidenceExtraction", executor: "job", labelKey: "stage.extract.label", descriptionKey: "stage.extract.detail" },
  { id: "compare", capability: "contradictionDetection", executor: "job", labelKey: "stage.compare.label", descriptionKey: "stage.compare.detail" },
  { id: "gaps", capability: "gapAnalysis", executor: "job", labelKey: "stage.gaps.label", descriptionKey: "stage.gaps.detail" },
  { id: "chapters", capability: "chapterDrafting", executor: "job", labelKey: "stage.chapters.label", descriptionKey: "stage.chapters.detail" },
  { id: "verify", capability: "documentExport", executor: "job", labelKey: "stage.verify.label", descriptionKey: "stage.verify.detail" },
];

/** Stages paired with their resolved status, ready for rendering. */
export type AcademicWorkflowStageView = AcademicWorkflowStage & {
  status: AcademicCapabilityStatus;
  phase: AcademicCapabilityPhase;
};

export function academicWorkflowViews(
  stages: readonly AcademicWorkflowStage[] = academicWorkflowStages,
): AcademicWorkflowStageView[] {
  // The downstream staged job ships as a capability of its own. While it is
  // `planned`, job-executed stages may not borrow runnability from a separate
  // available capability such as the read-only Sources search.
  const jobEngineAvailable = academicCapabilities.academicJob.status === "available";
  return stages.map((stage) => {
    const capability = capabilityOf(stage.capability);
    const runnable = capability.status === "available" && (stage.executor === "ui" || stage.executor === "planApi" || jobEngineAvailable);
    if (runnable) return { ...stage, status: "available" as const, phase: capability.phase };
    // An available capability whose job runner does not exist yet is downgraded
    // to a status label, never an executable stage; its phase is the delivery
    // phase in which the job pipeline itself arrives.
    if (capability.status === "available") {
      return { ...stage, status: "planned" as const, phase: academicCapabilities.academicJob.phase };
    }
    return { ...stage, status: capability.status, phase: capability.phase };
  });
}

export function availableWorkflowStageCount(views: readonly AcademicWorkflowStageView[]): number {
  return views.filter((stage) => stage.status === "available").length;
}
