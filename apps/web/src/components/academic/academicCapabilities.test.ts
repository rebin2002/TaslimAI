/**
 * Issue #1259 (Academic Research Studio) — capability registry tests.
 *
 * These tests are the guard rail behind the "no fake buttons" rule: anything the
 * academic surface mentions must be classified, and an unknown capability must
 * fail closed so a stale call site cannot imply a feature exists.
 */
import { describe, expect, it } from "vitest";
import {
  academicCapabilities,
  academicCapabilityIds,
  academicWorkflowStages,
  academicWorkflowViews,
  availableWorkflowStageCount,
  capabilityOf,
  isCapabilityAvailable,
  isCapabilityPlanned,
} from "./academicCapabilities";
import { asKey, createAcademicTranslator } from "./academicStrings";

const t = createAcademicTranslator("en");

describe("capability registry", () => {
  it("classifies every capability as available or planned with a phase", () => {
    expect(academicCapabilityIds.length).toBeGreaterThan(15);
    for (const id of academicCapabilityIds) {
      const capability = academicCapabilities[id];
      expect(["available", "planned", "unavailable"]).toContain(capability.status);
      expect(["A", "B", "C", "D"]).toContain(capability.phase);
      expect(capability.detailKey, id).toMatch(/^capability\./);
    }
  });

  it("has copy for every capability detail", () => {
    for (const id of academicCapabilityIds) {
      const text = t(asKey(academicCapabilities[id].detailKey));
      expect(text, id).not.toBe(academicCapabilities[id].detailKey);
      expect(text.trim().length).toBeGreaterThan(10);
    }
  });

  it("marks the four shipped Phase A surfaces as available", () => {
    expect(isCapabilityAvailable("briefSetup")).toBe(true);
    expect(isCapabilityAvailable("identifierValidation")).toBe(true);
    expect(isCapabilityAvailable("citationFormatting")).toBe(true);
    expect(isCapabilityAvailable("sourceReview")).toBe(true);
    expect(isCapabilityAvailable("standardResearch")).toBe(true);
  });

  it("marks record discovery and identifier lookup available now that the search endpoint ships", () => {
    // #1267 slice 1: a workspace-scoped, read-only search endpoint plus a panel that calls it.
    // That is the whole of what these two ids claim; nothing about reading content is claimed.
    expect(isCapabilityAvailable("scholarlyDiscovery")).toBe(true);
    expect(isCapabilityAvailable("isbnCatalogLookup")).toBe(true);
  });

  it("keeps every behaviour that still needs backend work out of the available set", () => {
    for (const id of ["fullTextRetrieval", "automatedScreening", "evidenceExtraction", "academicJob", "chapterDrafting", "documentExport", "budgetPreflight", "resumeCheckpoints"] as const) {
      expect(isCapabilityPlanned(id), id).toBe(true);
      expect(isCapabilityAvailable(id), id).toBe(false);
    }
  });

  it("fails closed on an unknown capability id", () => {
    expect(capabilityOf("runTheThesis").status).toBe("unavailable");
    expect(capabilityOf("").status).toBe("unavailable");
  });
});

describe("workflow stages", () => {
  it("lists the eight stages of the issue's bounded workflow in order", () => {
    expect(academicWorkflowStages.map((stage) => stage.id)).toEqual([
      "plan",
      "discovery",
      "screen",
      "extract",
      "compare",
      "gaps",
      "chapters",
      "verify",
    ]);
  });

  it("shows only the persisted Plan stage as available today", () => {
    const views = academicWorkflowViews();
    expect(views.filter((stage) => stage.status === "available").map((stage) => stage.id)).toEqual(["plan"]);
    expect(views.find((stage) => stage.id === "plan")!.executor).toBe("planApi");
    expect(availableWorkflowStageCount(views)).toBe(1);
    expect(views.every((stage) => stage.status === "planned" || stage.status === "available")).toBe(true);
    // Everything the staged job would run — discovery onward — is still a
    // promise, never a control, until the job pipeline itself ships.
    expect(views.filter((stage) => stage.status === "planned").map((stage) => stage.id)).toEqual(["discovery", "screen", "extract", "compare", "gaps", "chapters", "verify"]);
  });

  it("does not let the available discovery capability make its unconnected job stage runnable", () => {
    // Combined-tree integrity guard (#1267 slice 1 review): the workspace-scoped
    // read-only search endpoint genuinely ships scholarlyDiscovery as a
    // standalone capability, but nothing registers the AcademicWorkflow engine,
    // its stage handlers or its state store. A capability flip alone must never
    // turn an unconnected workflow stage into one that appears runnable.
    expect(isCapabilityAvailable("scholarlyDiscovery")).toBe(true);
    expect(isCapabilityPlanned("academicJob")).toBe(true);
    const discovery = academicWorkflowViews().find((stage) => stage.id === "discovery");
    expect(discovery).toBeDefined();
    expect(discovery!.executor).toBe("job");
    expect(discovery!.status).toBe("planned");
    // The downgraded stage reports the phase in which its runner arrives, not
    // the phase in which the underlying capability shipped.
    expect(discovery!.phase).toBe(academicCapabilities.academicJob.phase);
    // General invariant: nothing shows as available unless its local/API
    // executor exists or the downstream job engine is wired.
    for (const stage of academicWorkflowViews()) {
      if (stage.status === "available") {
        expect(stage.executor === "ui" || stage.executor === "planApi" || isCapabilityAvailable("academicJob"), stage.id).toBe(true);
      }
    }
  });

  it("applies the runner rule to any stage list, not only the shipped one", () => {
    // Same available capability, two executors: only the surface-executed stage
    // is runnable while the job pipeline is planned.
    const views = academicWorkflowViews([
      { id: "synthetic-ui", capability: "scholarlyDiscovery", executor: "ui", labelKey: "stage.discovery.label", descriptionKey: "stage.discovery.detail" },
      { id: "synthetic-job", capability: "scholarlyDiscovery", executor: "job", labelKey: "stage.discovery.label", descriptionKey: "stage.discovery.detail" },
    ]);
    expect(views.find((stage) => stage.id === "synthetic-ui")!.status).toBe("available");
    expect(views.find((stage) => stage.id === "synthetic-job")!.status).toBe("planned");
  });

  it("keeps every job-executed stage non-runnable while the job capability is planned", () => {
    const jobStages = academicWorkflowStages.filter((stage) => stage.executor === "job");
    expect(jobStages.map((stage) => stage.id)).toEqual(["discovery", "screen", "extract", "compare", "gaps", "chapters", "verify"]);
    const runnableJobStages = academicWorkflowViews().filter((stage) => stage.executor === "job" && stage.status === "available");
    expect(runnableJobStages.map((stage) => stage.id)).toEqual([]);
  });

  it("has customer-facing copy for each stage label and description", () => {
    for (const stage of academicWorkflowViews()) {
      expect(t(asKey(stage.labelKey)), stage.id).not.toBe(stage.labelKey);
      expect(t(asKey(stage.descriptionKey)), stage.id).not.toBe(stage.descriptionKey);
    }
  });
});
