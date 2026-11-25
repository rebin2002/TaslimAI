// @vitest-environment jsdom
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, api, type AcademicProjectDto, type AcademicProjectSummary } from "@/lib/api";
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

function approvedProject(projectId: string, state: string): AcademicProjectDto {
  const pending = project(projectId);
  return {
    ...pending,
    project: { ...pending.project, state, revision: 2 },
    pendingApproval: null,
    approvals: [{
      kind: "Plan",
      checkpointRevision: 1,
      decision: "Approved",
      notes: null,
      decidedAtUtc: now,
    }],
  };
}

function buttonNamed(container: HTMLElement, label: string): HTMLButtonElement {
  const button = Array.from(container.querySelectorAll("button")).find((candidate) => candidate.textContent?.trim() === label);
  if (!button) throw new Error(`Expected a button named "${label}"`);
  return button;
}

async function mountPanel() {
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
  return translate;
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

describe("Academic workflow create idempotency and conflict refresh", () => {
  it("uses a fresh key for each successful logical Create", async () => {
    vi.spyOn(api, "listAcademicProjects").mockResolvedValue([]);
    let createCount = 0;
    const create = vi.spyOn(api, "createAcademicProject").mockImplementation(async () => {
      createCount += 1;
      return project(`created-${createCount}`);
    });
    vi.spyOn(api, "runAcademicProject").mockImplementation(async (projectId) => project(projectId));

    const translate = await mountPanel();
    await act(async () => {
      buttonNamed(container, translate("workflow.createAction")).click();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await act(async () => {
      buttonNamed(container, translate("workflow.createAction")).click();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    expect(create).toHaveBeenCalledTimes(2);
    const firstKey = create.mock.calls[0][1].requestKey;
    const secondKey = create.mock.calls[1][1].requestKey;
    expect(firstKey).toBeTruthy();
    expect(secondKey).toBeTruthy();
    expect(secondKey).not.toBe(firstKey);
  });

  it("retains the same key when retrying a Create whose response failed", async () => {
    vi.spyOn(api, "listAcademicProjects").mockResolvedValue([]);
    const create = vi.spyOn(api, "createAcademicProject")
      .mockRejectedValueOnce(new Error("request interrupted"))
      .mockResolvedValueOnce(project("created-after-retry"));
    vi.spyOn(api, "runAcademicProject").mockResolvedValue(project("created-after-retry"));

    const translate = await mountPanel();
    await act(async () => {
      buttonNamed(container, translate("workflow.createAction")).click();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    await act(async () => {
      buttonNamed(container, translate("workflow.createAction")).click();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[1][1].requestKey).toBe(create.mock.calls[0][1].requestKey);
  });

  it("reloads after an approval 409 and replaces the stale approval action", async () => {
    const latest = deferred<AcademicProjectDto>();
    const getProject = vi.spyOn(api, "getAcademicProject")
      .mockResolvedValueOnce(project("approval-conflict"))
      .mockReturnValueOnce(latest.promise);
    vi.spyOn(api, "listAcademicProjects").mockResolvedValue([summary("approval-conflict")]);
    vi.spyOn(api, "submitAcademicApproval").mockRejectedValue(new ApiError(409, "checkpoint changed"));

    const translate = await mountPanel();
    expect(container.textContent).toContain(translate("workflow.approveAction"));
    await act(async () => {
      buttonNamed(container, translate("workflow.approveAction")).click();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    expect(getProject).toHaveBeenCalledTimes(2);
    expect(container.textContent).toContain(translate("workflow.conflictReloading"));
    expect(container.textContent).not.toContain(translate("workflow.approveAction"));
    await act(async () => {
      latest.resolve(approvedProject("approval-conflict", "Running"));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(container.textContent).toContain(translate("workflow.conflictReloaded"));
    expect(container.textContent).not.toContain(translate("workflow.approveAction"));
    expect(container.textContent).toContain(translate("workflow.resumeAction"));
  });

  it("reloads after a resume 409 and removes the stale resume action", async () => {
    const latest = deferred<AcademicProjectDto>();
    const getProject = vi.spyOn(api, "getAcademicProject")
      .mockResolvedValueOnce(approvedProject("resume-conflict", "Running"))
      .mockReturnValueOnce(latest.promise);
    vi.spyOn(api, "listAcademicProjects").mockResolvedValue([summary("resume-conflict")]);
    vi.spyOn(api, "runAcademicProject").mockRejectedValue(new ApiError(409, "checkpoint changed"));

    const translate = await mountPanel();
    expect(container.textContent).toContain(translate("workflow.resumeAction"));
    await act(async () => {
      buttonNamed(container, translate("workflow.resumeAction")).click();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    expect(getProject).toHaveBeenCalledTimes(2);
    expect(container.textContent).toContain(translate("workflow.conflictReloading"));
    expect(container.textContent).not.toContain(translate("workflow.resumeAction"));
    await act(async () => {
      latest.resolve(approvedProject("resume-conflict", "Completed"));
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(container.textContent).toContain(translate("workflow.conflictReloaded"));
    expect(container.textContent).not.toContain(translate("workflow.resumeAction"));
  });
});
