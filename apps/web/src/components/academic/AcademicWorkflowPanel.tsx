"use client";

import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiError, api, type AcademicProjectCreateInput, type AcademicProjectDto, type AcademicProjectSummary, type AcademicResearchPlan } from "@/lib/api";
import type { AcademicBrief, Methodology } from "./academicBrief";
import type { AcademicSourceType, CitationStyleId } from "./academicSourceLibrary";
import { asKey, type AcademicLabelers, type AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

const methodologyCode: Record<Methodology, string> = {
  systematic_review: "SystematicReview",
  narrative_review: "NarrativeReview",
  scoping_review: "ScopingReview",
  qualitative: "Qualitative",
  quantitative: "Quantitative",
  mixed_methods: "MixedMethods",
  case_study: "CaseStudy",
  comparative: "Comparative",
  doctrinal_legal: "DoctrinalLegal",
  secondary_desktop: "SecondaryDesktop",
};

const citationStyleCode: Record<CitationStyleId, string> = {
  apa7: "Apa7",
  harvard: "Harvard",
  chicago: "Chicago",
  ieee: "Ieee",
  vancouver: "Vancouver",
};

const stageLabelKeys: Record<string, string> = {
  Plan: "stage.plan.label",
  Discover: "stage.discovery.label",
  Screen: "stage.screen.label",
  Extract: "stage.extract.label",
  Synthesize: "stage.compare.label",
  DraftChapter: "stage.chapters.label",
  Verify: "stage.verify.label",
};
const ACADEMIC_PROJECT_PAGE_SIZE = 100;

function requestKey(): string {
  return globalThis.crypto?.randomUUID?.() ?? `academic-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

function toSnakeCase(value: string): string {
  return value.replace(/[A-Z]/g, (letter) => `_${letter.toLowerCase()}`).replace(/^_/, "");
}

function safeAcademicError(cause: unknown, t: AcademicTranslate): string {
  if (cause instanceof ApiError && cause.status === 501) return t("workflow.discoverNotConnected");
  if (cause instanceof ApiError && (cause.status === 403 || cause.status === 404)) return t("workflow.accessChanged");
  return t("workflow.actionFailure");
}

export function isCurrentAcademicWorkspaceOperation(
  operationWorkspaceId: string | null,
  operationGeneration: number,
  currentWorkspaceId: string | null,
  currentGeneration: number,
): boolean {
  return operationWorkspaceId === currentWorkspaceId && operationGeneration === currentGeneration;
}

export function isCurrentAcademicProjectIntent(
  operationWorkspaceId: string | null,
  operationWorkspaceGeneration: number,
  operationIntentGeneration: number,
  currentWorkspaceId: string | null,
  currentWorkspaceGeneration: number,
  currentIntentGeneration: number,
): boolean {
  return isCurrentAcademicWorkspaceOperation(
    operationWorkspaceId, operationWorkspaceGeneration, currentWorkspaceId, currentWorkspaceGeneration,
  ) && operationIntentGeneration === currentIntentGeneration;
}

export function isSelectedAcademicProject(
  projectId: string | null | undefined,
  selectedProjectId: string,
): boolean {
  return Boolean(projectId && projectId === selectedProjectId);
}

export function toCreateInput(brief: AcademicBrief, chapterHeadings: readonly string[], language: string, key: string): AcademicProjectCreateInput {
  const methods = brief.methodologies.map((method) => methodologyCode[method]);
  return {
    requestKey: key,
    researchQuestion: brief.researchQuestion.trim(),
    discipline: brief.discipline,
    level: brief.level === "doctoral" ? "Doctoral" : "Masters",
    method: methods[0] ?? "NarrativeReview",
    methodologies: methods,
    citationStyle: citationStyleCode[brief.citationStyle],
    language: language === "ar" || language === "ku" ? language : "en",
    targetChapterHeadings: chapterHeadings.map((heading) => heading.trim()).filter(Boolean).slice(0, 24),
    workingTitle: brief.workingTitle.trim() || null,
    geographicScope: brief.geographicScope.trim() || null,
    populationOrContext: brief.populationOrContext.trim() || null,
    timePeriod: brief.timePeriod.trim() || null,
    subQuestions: brief.reviewQuestions.map((question) => question.trim()).filter(Boolean).slice(0, 4),
    keywords: brief.keywords.map((keyword) => keyword.trim()).filter(Boolean),
    minimumSources: brief.requirements.minimumSources,
    minimumPeerReviewed: brief.requirements.minimumPeerReviewed,
    allowedSourceTypes: brief.requirements.allowedSourceTypes,
    requireFullText: brief.requirements.requireFullText,
    programmeRequirements: brief.requirements.referencingGuideNotes.trim() || null,
  };
}

export function formatSavedProjectContext(discipline: string, level: string, revision: number, labels: AcademicLabelers, t: AcademicTranslate): string {
  return `${labels.discipline(discipline)} · ${level} · ${t("workflow.revision", { revision: String(revision) })}`;
}

export function formatPlanSourceTargets(plan: AcademicResearchPlan, t: AcademicTranslate): string | null {
  if (plan.minimumSources === null && plan.minimumPeerReviewed === null) return null;
  return t("workflow.sourceTargetsValue", {
    total: plan.minimumSources === null ? t("workflow.notSpecified") : String(plan.minimumSources),
    peer: plan.minimumPeerReviewed === null ? t("workflow.notSpecified") : String(plan.minimumPeerReviewed),
  });
}

export function formatPlanKeywords(plan: AcademicResearchPlan): string | null {
  return plan.keywords.length > 0 ? plan.keywords.join(", ") : null;
}

export type AcademicWorkflowPanelProps = {
  workspaceId: string | null;
  brief: AcademicBrief;
  chapterHeadings: readonly string[];
  language: string;
  canStart: boolean;
  t: AcademicTranslate;
  labels: AcademicLabelers;
};

export function AcademicWorkflowPanel({ workspaceId, brief, chapterHeadings, language, canStart, t, labels }: AcademicWorkflowPanelProps) {
  const [projects, setProjects] = useState<AcademicProjectSummary[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const [project, setProject] = useState<AcademicProjectDto | null>(null);
  const [hasMoreProjects, setHasMoreProjects] = useState(false);
  const [nextProjectPage, setNextProjectPage] = useState(2);
  const [listing, setListing] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const requestKeyRef = useRef<{ signature: string; key: string } | null>(null);
  const workspaceIdRef = useRef(workspaceId);
  const workspaceGenerationRef = useRef(0);
  const intentGenerationRef = useRef(0);
  const selectedIdRef = useRef(selectedId);
  const translateRef = useRef(t);

  useLayoutEffect(() => {
    translateRef.current = t;
  }, [t]);

  // The parent keys this panel by workspace, so UI state resets by remount. This
  // also fences any in-flight action if a caller reuses the instance across workspaces.
  useLayoutEffect(() => {
    const workspaceChanged = workspaceIdRef.current !== workspaceId;
    workspaceIdRef.current = workspaceId;
    workspaceGenerationRef.current += 1;
    intentGenerationRef.current += 1;
    requestKeyRef.current = null;
    if (workspaceChanged) {
      selectedIdRef.current = "";
      setSelectedId("");
      setProject(null);
      setProjects([]);
      setHasMoreProjects(false);
      setNextProjectPage(2);
      setListing(Boolean(workspaceId));
      setBusy(false);
      setError("");
    }
  }, [workspaceId]);

  useEffect(() => {
    let active = true;
    if (!workspaceId) return () => { active = false; };
    const operationWorkspaceGeneration = workspaceGenerationRef.current;
    const operationIntentGeneration = intentGenerationRef.current;
    const isCurrentOperation = () => active && isCurrentAcademicProjectIntent(
      workspaceId, operationWorkspaceGeneration, operationIntentGeneration,
      workspaceIdRef.current, workspaceGenerationRef.current, intentGenerationRef.current,
    );

    void api.listAcademicProjects(workspaceId, 1, ACADEMIC_PROJECT_PAGE_SIZE).then(async (saved) => {
      if (!isCurrentOperation()) return;
      setProjects(saved);
      setHasMoreProjects(saved.length === ACADEMIC_PROJECT_PAGE_SIZE);
      setNextProjectPage(2);
      setListing(false);
      const latest = saved[0];
      if (!latest) return;
      selectedIdRef.current = latest.projectId;
      setSelectedId(latest.projectId);
      setProject(null);
      const loaded = await api.getAcademicProject(latest.projectId);
      if (isCurrentOperation() && selectedIdRef.current === latest.projectId
        && loaded.project.projectId === latest.projectId && loaded.project.workspaceId === workspaceId) {
        setProject(loaded);
      }
    }).catch(() => {
      if (isCurrentOperation()) {
        setListing(false);
        setError(translateRef.current("workflow.loadFailure"));
      }
    });
    return () => { active = false; };
  }, [workspaceId]);

  async function loadMoreProjects() {
    if (!workspaceId || !hasMoreProjects || listing || busy) return;
    const operationGeneration = workspaceGenerationRef.current;
    const operationPage = nextProjectPage;
    const isCurrentOperation = () => isCurrentAcademicWorkspaceOperation(
      workspaceId, operationGeneration, workspaceIdRef.current, workspaceGenerationRef.current,
    );
    setListing(true);
    setError("");
    try {
      const saved = await api.listAcademicProjects(workspaceId, operationPage, ACADEMIC_PROJECT_PAGE_SIZE);
      if (!isCurrentOperation()) return;
      setProjects((current) => {
        const byId = new Map(current.map((item) => [item.projectId, item]));
        for (const item of saved) byId.set(item.projectId, item);
        return [...byId.values()].sort((left, right) => right.updatedAtUtc.localeCompare(left.updatedAtUtc));
      });
      setHasMoreProjects(saved.length === ACADEMIC_PROJECT_PAGE_SIZE);
      setNextProjectPage(operationPage + 1);
    } catch {
      if (isCurrentOperation()) setError(t("workflow.loadFailure"));
    } finally {
      if (isCurrentOperation()) setListing(false);
    }
  }

  function remember(next: AcademicProjectDto) {
    selectedIdRef.current = next.project.projectId;
    setProject(next);
    setSelectedId(next.project.projectId);
    setProjects((current) => [next.project, ...current.filter((item) => item.projectId !== next.project.projectId)]
      .sort((left, right) => right.updatedAtUtc.localeCompare(left.updatedAtUtc)));
  }

  async function createAndRun() {
    if (!workspaceId || !canStart || busy || listing) return;
    const operationWorkspaceGeneration = workspaceGenerationRef.current;
    const operationIntentGeneration = ++intentGenerationRef.current;
    selectedIdRef.current = "";
    setSelectedId("");
    setProject(null);
    const isCurrentOperation = () => isCurrentAcademicProjectIntent(
      workspaceId, operationWorkspaceGeneration, operationIntentGeneration,
      workspaceIdRef.current, workspaceGenerationRef.current, intentGenerationRef.current,
    );
    const draft = toCreateInput(brief, chapterHeadings, language, "");
    const signature = JSON.stringify({ ...draft, requestKey: undefined });
    if (!requestKeyRef.current || requestKeyRef.current.signature !== signature) {
      requestKeyRef.current = { signature, key: requestKey() };
    }
    const input = { ...draft, requestKey: requestKeyRef.current.key };
    setBusy(true);
    setError("");
    try {
      const created = await api.createAcademicProject(workspaceId, input);
      if (!isCurrentOperation()) return;
      if (created.project.workspaceId !== workspaceId) {
        setError(t("workflow.accessChanged"));
        return;
      }
      remember(created);
      const prepared = await api.runAcademicProject(created.project.projectId);
      if (isCurrentOperation() && prepared.project.projectId === created.project.projectId
        && prepared.project.workspaceId === workspaceId) remember(prepared);
    } catch (cause) {
      if (!isCurrentOperation()) return;
      setError(safeAcademicError(cause, t));
      if (requestKeyRef.current?.key) {
        try {
          const rows = await api.listAcademicProjects(workspaceId);
          if (!isCurrentOperation()) return;
          setProjects(rows);
          const match = rows.find((row) => row.requestKey === requestKeyRef.current?.key);
          if (match) {
            const recovered = await api.getAcademicProject(match.projectId);
            if (isCurrentOperation() && recovered.project.projectId === match.projectId
              && recovered.project.workspaceId === workspaceId) remember(recovered);
          }
        } catch { /* Keep the primary action error; the next load can recover from server state. */ }
      }
    } finally {
      if (isCurrentOperation()) setBusy(false);
    }
  }

  async function loadSelected() {
    const targetProjectId = selectedIdRef.current;
    if (!targetProjectId || busy) return;
    const operationWorkspaceId = workspaceId;
    const operationWorkspaceGeneration = workspaceGenerationRef.current;
    const operationIntentGeneration = ++intentGenerationRef.current;
    const isCurrentOperation = () => isCurrentAcademicProjectIntent(
      operationWorkspaceId, operationWorkspaceGeneration, operationIntentGeneration,
      workspaceIdRef.current, workspaceGenerationRef.current, intentGenerationRef.current,
    ) && selectedIdRef.current === targetProjectId;
    setBusy(true);
    setError("");
    try {
      const loaded = await api.getAcademicProject(targetProjectId);
      if (!isCurrentOperation()) return;
      if (loaded.project.projectId === targetProjectId && loaded.project.workspaceId === operationWorkspaceId) remember(loaded);
      else setError(t("workflow.accessChanged"));
    } catch (cause) {
      if (isCurrentOperation()) setError(safeAcademicError(cause, t));
    } finally {
      if (isCurrentOperation()) setBusy(false);
    }
  }

  async function resume() {
    const targetProjectId = project?.project.projectId;
    if (!project || !targetProjectId || busy || project.pendingApproval
      || !isSelectedAcademicProject(targetProjectId, selectedId) || selectedIdRef.current !== targetProjectId) return;
    const operationWorkspaceId = workspaceId;
    const operationWorkspaceGeneration = workspaceGenerationRef.current;
    const operationIntentGeneration = ++intentGenerationRef.current;
    const isCurrentOperation = () => isCurrentAcademicProjectIntent(
      operationWorkspaceId, operationWorkspaceGeneration, operationIntentGeneration,
      workspaceIdRef.current, workspaceGenerationRef.current, intentGenerationRef.current,
    ) && selectedIdRef.current === targetProjectId;
    setBusy(true);
    setError("");
    try {
      const resumed = await api.runAcademicProject(targetProjectId);
      if (!isCurrentOperation()) return;
      if (resumed.project.projectId === targetProjectId && resumed.project.workspaceId === operationWorkspaceId) remember(resumed);
    } catch (cause) {
      if (!isCurrentOperation()) return;
      setError(safeAcademicError(cause, t));
      if (cause instanceof ApiError && cause.status === 501) {
        try {
          const latest = await api.getAcademicProject(targetProjectId);
          if (isCurrentOperation() && latest.project.projectId === targetProjectId
            && latest.project.workspaceId === operationWorkspaceId) remember(latest);
        } catch { /* Preserve the clear not-connected status. */ }
      }
    } finally {
      if (isCurrentOperation()) setBusy(false);
    }
  }

  async function approvePlan() {
    const targetProjectId = project?.project.projectId;
    if (!project || !targetProjectId || project.pendingApproval?.kind !== "Plan" || busy
      || !isSelectedAcademicProject(targetProjectId, selectedId)
      || selectedIdRef.current !== targetProjectId) return;
    const operationWorkspaceId = workspaceId;
    const operationWorkspaceGeneration = workspaceGenerationRef.current;
    const operationIntentGeneration = ++intentGenerationRef.current;
    const isCurrentOperation = () => isCurrentAcademicProjectIntent(
      operationWorkspaceId, operationWorkspaceGeneration, operationIntentGeneration,
      workspaceIdRef.current, workspaceGenerationRef.current, intentGenerationRef.current,
    ) && selectedIdRef.current === targetProjectId;
    setBusy(true);
    setError("");
    try {
      const approved = await api.submitAcademicApproval(targetProjectId, "Plan", "Approved");
      if (!isCurrentOperation()) return;
      if (approved.project.projectId === targetProjectId && approved.project.workspaceId === operationWorkspaceId) remember(approved);
      else setError(t("workflow.accessChanged"));
    } catch (cause) {
      if (isCurrentOperation()) setError(safeAcademicError(cause, t));
    } finally {
      if (isCurrentOperation()) setBusy(false);
    }
  }

  const displayedProject = project && isSelectedAcademicProject(project.project.projectId, selectedId) ? project : null;
  const plan = displayedProject?.plan ?? null;
  const planApproved = Boolean(displayedProject?.approvals.some((approval) => approval.kind === "Plan" && approval.decision === "Approved"));
  const nextStageMissing = Boolean(plan && planApproved && displayedProject?.missingStages.includes("Discover"));

  return (
    <section className={styles.workflowPanel} aria-labelledby="academic-workflow-heading">
      <div className={styles.workflowHeader}>
        <div>
          <h3 id="academic-workflow-heading" className={styles.sectionHeading}>{t("workflow.title")}</h3>
          <p className={styles.sectionIntro}>{t("workflow.intro")}</p>
        </div>
        <span className={styles.workflowScope}>{t("workflow.workspaceScoped")}</span>
      </div>

      {!workspaceId ? (
        <p className={styles.workflowNotice} role="status">{t("workflow.workspaceRequired")}</p>
      ) : (
        <>
          <div className={styles.workflowControls}>
            <label className={styles.workflowSelectLabel}>
              <span>{t("workflow.savedProjects")}</span>
              <select value={selectedId} onChange={(event) => {
                const nextId = event.target.value;
                if (nextId === selectedIdRef.current) return;
                intentGenerationRef.current += 1;
                selectedIdRef.current = nextId;
                setSelectedId(nextId);
                setProject(null);
                setError("");
              }} disabled={listing || busy}>
                <option value="">{listing ? t("workflow.loadingProjects") : t("workflow.selectProject")}</option>
                {projects.map((item) => (
                  <option key={item.projectId} value={item.projectId}>
                    {item.researchQuestion} · {item.state}
                  </option>
                ))}
              </select>
            </label>
            <button type="button" className={styles.secondaryButton} onClick={() => void loadSelected()} disabled={!selectedId || busy || listing}>
              {busy ? t("workflow.working") : t("workflow.loadAction")}
            </button>
          </div>
          {hasMoreProjects ? (
            <button type="button" className={styles.secondaryButton} onClick={() => void loadMoreProjects()} disabled={listing || busy}>
              {listing ? t("workflow.loadingProjects") : t("workflow.loadMoreProjects")}
            </button>
          ) : null}
          {!listing && projects.length === 0 ? <p className={styles.workflowSubtle}>{t("workflow.noSavedProjects")}</p> : null}
          {!canStart ? <p className={styles.workflowSubtle}>{t("workflow.briefBlocked")}</p> : null}
          <button type="button" className={styles.primaryButton} onClick={() => void createAndRun()} disabled={!canStart || busy || listing}>
            {busy ? t("workflow.working") : t("workflow.createAction")}
          </button>
          {error ? <p className={styles.workflowError} role="alert">{error}</p> : null}

          {displayedProject ? (
            <div className={styles.workflowProject}>
              <div className={styles.workflowProjectHeading}>
                <div>
                  <p className={styles.workflowKicker}>{t("workflow.savedPlan")}</p>
                  <h4>{plan?.workingTitle || displayedProject.project.researchQuestion}</h4>
                  <p className={styles.workflowSubtle}>{formatSavedProjectContext(displayedProject.project.discipline, displayedProject.project.level, displayedProject.project.revision, labels, t)}</p>
                </div>
                <span className={styles.workflowState}>{displayedProject.project.state}</span>
              </div>

              {plan ? <PlanArtifact plan={plan} t={t} labels={labels} /> : (
                <p className={styles.workflowNotice} role="status">{t("workflow.planNotPrepared")}</p>
              )}

              <div>
                <h5 className={styles.workflowSubheading}>{t("workflow.stages")}</h5>
                <ol className={styles.workflowStageList}>
                  {displayedProject.stages.map((stage) => (
                    <li key={stage.stage} data-state={stage.state}>
                      <span>{stageLabelKeys[stage.stage] ? t(asKey(stageLabelKeys[stage.stage])) : stage.stage}</span>
                      <strong>{t(asKey(`workflow.stageStatus.${stage.state.toLowerCase()}`))}</strong>
                    </li>
                  ))}
                </ol>
              </div>

              {displayedProject.pendingApproval?.kind === "Plan" ? (
                <div className={styles.workflowApproval}>
                  <div>
                    <h5 className={styles.workflowSubheading}>{t("workflow.reviewTitle")}</h5>
                    <p className={styles.workflowSubtle}>{t("workflow.reviewPrompt")}</p>
                    <ul>{displayedProject.pendingApproval.reviewerPrompts.map((prompt, index) => <li key={`${index}-${prompt}`}>{prompt}</li>)}</ul>
                  </div>
                  <button type="button" className={styles.primaryButton} onClick={() => void approvePlan()} disabled={busy || !plan}>
                    {busy ? t("workflow.working") : t("workflow.approveAction")}
                  </button>
                </div>
              ) : null}

              {nextStageMissing ? <p className={styles.workflowNotice} role="status">{t("workflow.discoverNotConnected")}</p> : null}
              {planApproved && !displayedProject.pendingApproval && displayedProject.project.state === "Running" ? (
                <button type="button" className={styles.secondaryButton} onClick={() => void resume()} disabled={busy}>
                  {busy ? t("workflow.working") : t("workflow.resumeAction")}
                </button>
              ) : null}
            </div>
          ) : null}
        </>
      )}
    </section>
  );
}

function PlanArtifact({ plan, t, labels }: { plan: AcademicResearchPlan; t: AcademicTranslate; labels: AcademicLabelers }) {
  const methodologies = plan.methodologies.map((method) => t(asKey(`methodology.${toSnakeCase(method)}`))).join(", ");
  const citationStyle = t(asKey(`style.${toSnakeCase(plan.citationStyle)}`));
  const sourceTypeLabels = plan.allowedSourceTypes.map((type) => labels.sourceType(type as AcademicSourceType)).join(", ");
  const sourceTargets = formatPlanSourceTargets(plan, t);
  const keywords = formatPlanKeywords(plan);
  return (
    <div className={styles.workflowArtifact}>
      <h5 className={styles.workflowSubheading}>{t("workflow.planArtifact")}</h5>
      <p><strong>{t("workflow.question")}</strong> {plan.researchQuestion}</p>
      <p><strong>{t("workflow.methodAndStyle")}</strong> {methodologies || labels.methodology("narrative_review")} · {citationStyle}</p>
      {keywords ? <p><strong>{t("summary.keywords")}</strong> {keywords}</p> : null}
      {plan.geographicScope ? <p><strong>{t("summary.scope")}</strong> {plan.geographicScope}</p> : null}
      {plan.populationOrContext ? <p><strong>{t("summary.population")}</strong> {plan.populationOrContext}</p> : null}
      {plan.timePeriod ? <p><strong>{t("summary.period")}</strong> {plan.timePeriod}</p> : null}
      {plan.subQuestions.length > 0 ? <PlanList title={t("workflow.subQuestions")} items={plan.subQuestions} /> : null}
      {plan.objectives.length > 0 ? <PlanList title={t("workflow.objectives")} items={plan.objectives} /> : null}
      {plan.inclusionCriteria.length > 0 ? <PlanList title={t("workflow.inclusion")} items={plan.inclusionCriteria} /> : null}
      {plan.exclusionCriteria.length > 0 ? <PlanList title={t("workflow.exclusion")} items={plan.exclusionCriteria} /> : null}
      {sourceTypeLabels ? <p><strong>{t("workflow.allowedSources")}</strong> {sourceTypeLabels}</p> : null}
      {sourceTargets !== null ? <p><strong>{t("workflow.sourceTargets")}</strong> {sourceTargets}</p> : null}
      {plan.requireFullText ? <p>{t("workflow.fullTextRequirement")}</p> : null}
      {plan.programmeRequirements ? <p><strong>{t("workflow.programmeRules")}</strong> {plan.programmeRequirements}</p> : null}
      {plan.chapterOutline.length > 0 ? <PlanList title={t("workflow.chapters")} items={plan.chapterOutline} /> : null}
      <div>
        <h6>{t("workflow.searchStrategy")}</h6>
        <ul className={styles.workflowSearchList}>
          {plan.searchStrategy.map((search, index) => (
            <li key={`${search.collection}-${index}`}>
              <strong>{t(asKey(`workflow.collection.${search.collection}`))}</strong>
              <span>{search.query}</span>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

function PlanList({ title, items }: { title: string; items: string[] }) {
  return <div className={styles.workflowListBlock}><h6>{title}</h6><ul>{items.map((item, index) => <li key={`${index}-${item}`}>{item}</li>)}</ul></div>;
}
