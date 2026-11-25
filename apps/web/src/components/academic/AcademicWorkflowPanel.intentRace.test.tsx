// @vitest-environment jsdom
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, type AcademicProjectDto, type AcademicProjectSummary } from "@/lib/api";
import { createDefaultBrief } from "./academicBrief";
import { createAcademicLabels, createAcademicTranslator } from "./academicStrings";
import { AcademicWorkflowPanel } from "./AcademicWorkflowPanel";

const workspaceId = "synthetic-workspace";
const now = "2026-11-25T00:00:00Z";

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => { resolve = resolvePromise; });
  return { promise, resolve };
}

function summary(projectId: string): AcademicProjectSummary {
  return {
    projectId,
    workspaceId,
    requestKey: `request-${projectId}`,
    researchQuestion: `Synthetic market-access question ${projectId}`,
    discipline: "pharmacy_and_pharmacotherapy",
    level: "Masters",
    state: "PlanApprovalPending",
    stateCode: null,
    revision: 1,
    createdAtUtc: now,
    updatedAtUtc: now,
  };
}

function project(projectId: string): AcademicProjectDto {
  return {
    project: summary(projectId),
    stages: [{
      stage: "Plan",
      state: "Completed",
      attemptCount: 1,
      outputCheckpointRevision: 1,
      lastErrorCode: null,
      lastErrorDetail: null,
      startedAtUtc: now,
      completedAtUtc: now,
    }],
    pendingApproval: {
      kind: "Plan",
      approvalKey: `${projectId}:Plan:1`,
      checkpointRevision: 1,
      requestedAtUtc: now,
      reviewerPrompts: [`Review the synthetic Plan ${projectId}`],
    },
    approvals: [],
    usage: { spentUsd: 0, ceilingUsd: 0, remainingUsd: 0, callsUsed: 0, callCeiling: 0, callsRemaining: 0 },
    connectedStages: ["Plan"],
    missingStages: ["Discover"],
    evidenceCurrent: true,
    planVersion: 1,
    sourceCount: 0,
    includedCount: 0,
    excludedCount: 0,
    undeterminedCount: 0,
    evidenceCount: 0,
    themeCount: 0,
    chapterCount: 0,
    verification: null,
    plan: {
      planVersion: 1,
      researchQuestion: `Synthetic market-access question ${projectId}`,
      workingTitle: `Plan ${projectId}`,
      level: "Masters",
      method: "NarrativeReview",
      methodologies: ["NarrativeReview"],
      citationStyle: "Harvard",
      objectives: [],
      inclusionCriteria: [],
      exclusionCriteria: [],
      searchStrategy: [],
      chapterOutline: [],
      geographicScope: "Kurdistan Region of Iraq",
      populationOrContext: null,
      timePeriod: "2015-2026",
      subQuestions: [],
      keywords: [],
      minimumSources: 60,
      minimumPeerReviewed: 40,
      allowedSourceTypes: ["journal_article", "book"],
      requireFullText: true,
      programmeRequirements: null,
    },
    code: null,
  };
}

function buttonNamed(container: HTMLElement, label: string): HTMLButtonElement {
  const button = Array.from(container.querySelectorAll("button")).find((candidate) => candidate.textContent?.trim() === label);
  if (!button) throw new Error(`Expected a button named "${label}"`);
  return button;
}

let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  container = document.createElement("div");
  document.body.appendChild(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("Academic workflow latest-intent guard", () => {
  it("keeps Plan B selected and approves B when the automatic get(A) resolves late", async () => {
    const initialProjectA = deferred<AcademicProjectDto>();
    const projects = [summary("project-a"), summary("project-b")];
    const getProject = vi.spyOn(api, "getAcademicProject").mockImplementation((projectId) => {
      if (projectId === "project-a") return initialProjectA.promise;
      if (projectId === "project-b") return Promise.resolve(project("project-b"));
      return Promise.reject(new Error(`Unexpected project ${projectId}`));
    });
    vi.spyOn(api, "listAcademicProjects").mockResolvedValue(projects);
    const submitApproval = vi.spyOn(api, "submitAcademicApproval").mockResolvedValue(project("project-b"));

    const translate = createAcademicTranslator("en");
    await act(async () => {
      root.render(
        <AcademicWorkflowPanel
          workspaceId={workspaceId}
          brief={createDefaultBrief("masters")}
          chapterHeadings={[]}
          language="en"
          canStart={true}
          t={translate}
          labels={createAcademicLabels(translate)}
        />,
      );
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    const select = container.querySelector("select");
    expect(select).not.toBeNull();
    expect(select?.value).toBe("project-a");
    expect(getProject).toHaveBeenCalledWith("project-a");

    await act(async () => {
      select!.value = "project-b";
      select!.dispatchEvent(new Event("change", { bubbles: true }));
    });
    expect(select!.value).toBe("project-b");

    await act(async () => {
      buttonNamed(container, translate("workflow.loadAction")).click();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(container.textContent).toContain("Plan project-b");

    await act(async () => {
      initialProjectA.resolve(project("project-a"));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    expect(select!.value).toBe("project-b");
    expect(container.textContent).toContain("Plan project-b");
    expect(container.textContent).not.toContain("Plan project-a");

    await act(async () => {
      buttonNamed(container, translate("workflow.approveAction")).click();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(submitApproval).toHaveBeenCalledTimes(1);
    expect(submitApproval).toHaveBeenCalledWith(
      "project-b",
      "Plan",
      "project-b:Plan:1",
      1,
      "Approved",
    );
  });
});
