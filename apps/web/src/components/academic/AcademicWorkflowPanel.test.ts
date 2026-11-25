import { describe, expect, it } from "vitest";
import { createDefaultBrief, type AcademicBrief } from "./academicBrief";
import { isCurrentAcademicProjectIntent, isCurrentAcademicWorkspaceOperation, isSelectedAcademicProject, toCreateInput, formatPlanKeywords, formatPlanSourceTargets, formatSavedProjectContext } from "./AcademicWorkflowPanel";
import { createAcademicLabels, createAcademicTranslator } from "./academicStrings";
import type { AcademicResearchPlan } from "@/lib/api";

describe("Academic workflow panel request projection", () => {
  it("preserves the synthetic Kurdistan pharma market-access brief and scholarly book/article choices", () => {
    const brief: AcademicBrief = {
      ...createDefaultBrief("masters"),
      discipline: "pharmacy_and_pharmacotherapy",
      citationStyle: "vancouver",
      methodologies: ["systematic_review", "qualitative"],
      workingTitle: "Pharmaceutical market access in the Kurdistan Region",
      researchQuestion: "How has pharmaceutical market access regulation evolved in the Kurdistan Region of Iraq since 2012?",
      reviewQuestions: ["Which institutions regulate medicine entry?"],
      keywords: ["pharmaceutical market access", "medicine regulation"],
      geographicScope: "Kurdistan Region of Iraq",
      populationOrContext: "Regulated medicines and patients",
      timePeriod: "2012-2025",
      requirements: {
        wordCount: 40000,
        minimumSources: 60,
        minimumPeerReviewed: 40,
        allowedSourceTypes: ["journal_article", "book"],
        requireFullText: true,
        referencingGuideNotes: "Use Vancouver style and keep an auditable screening log.",
      },
    };

    const input = toCreateInput(brief, ["Introduction", "Literature Review"], "ku", "synthetic-kurdistan-fixture");

    expect(input.requestKey).toBe("synthetic-kurdistan-fixture");
    expect(input.researchQuestion).toContain("pharmaceutical market access regulation");
    expect(input.discipline).toBe("pharmacy_and_pharmacotherapy");
    expect(input.level).toBe("Masters");
    expect(input.methodologies).toEqual(["SystematicReview", "Qualitative"]);
    expect(input.citationStyle).toBe("Vancouver");
    expect(input.geographicScope).toBe("Kurdistan Region of Iraq");
    expect(input.allowedSourceTypes).toEqual(["journal_article", "book"]);
    expect(input.minimumSources).toBe(60);
    expect(input.minimumPeerReviewed).toBe(40);
    expect(input.requireFullText).toBe(true);
    expect(input.language).toBe("ku");
  });

  it("preserves all keywords in the create mapping instead of clipping the payload", () => {
    const keywords = Array.from({ length: 13 }, (_, index) => `keyword-${index + 1}`);
    const brief: AcademicBrief = {
      ...createDefaultBrief(),
      discipline: "pharmacy_and_pharmacotherapy",
      methodologies: ["systematic_review"],
      researchQuestion: "How does a synthetic medicine policy affect access?",
      keywords,
    };

    expect(toCreateInput(brief, [], "en", "keyword-limit-test").keywords).toEqual(keywords);
  });

  it("renders saved discipline IDs through the localized labeler", () => {
    const t = createAcademicTranslator("en");
    const labels = createAcademicLabels(t);

    expect(formatSavedProjectContext("pharmacy_and_pharmacotherapy", "Masters", 5, labels, t))
      .toBe("Pharmacy and pharmacotherapy · Masters · checkpoint 5");
  });

  it("shows a peer-reviewed target even if the total source target is not specified", () => {
    const t = createAcademicTranslator("en");
    const plan = { minimumSources: null, minimumPeerReviewed: 40 } as AcademicResearchPlan;

    expect(formatPlanSourceTargets(plan, t)).toBe("not specified total; 40 peer-reviewed");
    expect(formatPlanSourceTargets({ ...plan, minimumPeerReviewed: null }, t)).toBeNull();
  });

  it("renders the complete saved keyword list independently of bounded search queries", () => {
    const plan = { keywords: ["market access", "reimbursement"] } as AcademicResearchPlan;

    expect(formatPlanKeywords(plan)).toBe("market access, reimbursement");
    expect(formatPlanKeywords({ ...plan, keywords: [] })).toBeNull();
  });
});

describe("Academic workflow workspace fencing", () => {
  it("rejects results from a prior workspace even if the same workspace is later selected again", () => {
    expect(isCurrentAcademicWorkspaceOperation("workspace-a", 3, "workspace-b", 4)).toBe(false);
    expect(isCurrentAcademicWorkspaceOperation("workspace-a", 3, "workspace-a", 5)).toBe(false);
    expect(isCurrentAcademicWorkspaceOperation("workspace-b", 4, "workspace-b", 4)).toBe(true);
  });

  it("rejects project results after a newer selection, load, or create intent", () => {
    expect(isCurrentAcademicProjectIntent("workspace-a", 3, 7, "workspace-a", 3, 8)).toBe(false);
    expect(isCurrentAcademicProjectIntent("workspace-a", 3, 7, "workspace-b", 4, 7)).toBe(false);
    expect(isCurrentAcademicProjectIntent("workspace-a", 3, 7, "workspace-a", 3, 7)).toBe(true);
  });

  it("allows approval only when the submitted project is both selected and displayed", () => {
    expect(isSelectedAcademicProject("project-b", "project-b")).toBe(true);
    expect(isSelectedAcademicProject("project-a", "project-b")).toBe(false);
    expect(isSelectedAcademicProject(null, "project-b")).toBe(false);
  });
});
