/**
 * Issue #1259 (Academic Research Studio) — Phase A brief model tests.
 *
 * Synthetic fixtures throughout. These pin the behaviour the academic entry
 * depends on: level and discipline selection, the suggestion mechanism that must
 * never overwrite a student's own answers, and validation that blocks only on
 * what genuinely makes a plan unusable.
 */
import { describe, expect, it } from "vitest";
import {
  academicLevels,
  applyDisciplineDefaults,
  briefLimits,
  citationStyles,
  countAccessLevels,
  createDefaultBrief,
  disciplines,
  findDiscipline,
  hasBlockingIssue,
  isDisciplineId,
  methodologyOptions,
  parseKeywords,
  summarizeBrief,
  validateAcademicBrief,
  type AcademicBrief,
} from "./academicBrief";
import { createLabelsFixture } from "./academicTestHelpers";

function briefFixture(overrides: Partial<AcademicBrief> = {}): AcademicBrief {
  const base: AcademicBrief = {
    ...createDefaultBrief("masters"),
    discipline: "pharmacy_and_pharmacotherapy",
    citationStyle: "vancouver",
    methodologies: ["systematic_review"],
    researchQuestion: "How has pharmaceutical market access regulation evolved in the Kurdistan Region of Iraq?",
    reviewQuestions: ["What pricing rule is described?"],
    requirements: {
      wordCount: 15000,
      minimumSources: 30,
      minimumPeerReviewed: 20,
      allowedSourceTypes: ["journal_article", "report", "thesis"],
      requireFullText: false,
      referencingGuideNotes: "",
    },
  };
  return { ...base, ...overrides };
}

const labels = createLabelsFixture();

describe("catalogues", () => {
  it("offers exactly the two study levels and five styles named in the issue", () => {
    expect(academicLevels).toEqual(["masters", "doctoral"]);
    expect(citationStyles).toEqual(["apa7", "harvard", "chicago", "ieee", "vancouver"]);
  });

  it("gives every discipline a label key, a suggested style and suggested methods", () => {
    expect(disciplines.length).toBeGreaterThan(10);
    for (const discipline of disciplines) {
      expect(discipline.labelKey, discipline.id).toMatch(/^discipline\./);
      expect(citationStyles).toContain(discipline.suggestedCitationStyle);
      expect(discipline.suggestedMethodologies.length, discipline.id).toBeGreaterThan(0);
      for (const methodology of discipline.suggestedMethodologies) expect(methodologyOptions).toContain(methodology);
      expect(discipline.suggestedSourceTypes.length, discipline.id).toBeGreaterThan(0);
    }
  });

  it("falls back to the catch-all discipline instead of throwing on unknown input", () => {
    expect(findDiscipline("not_a_discipline").id).toBe("other");
    expect(isDisciplineId("law_and_policy")).toBe(true);
    expect(isDisciplineId("DROP TABLE")).toBe(false);
  });
});

describe("defaults", () => {
  it("starts on the academic mode with no invented answers", () => {
    const brief = createDefaultBrief();
    expect(brief.mode).toBe("academic");
    expect(brief.level).toBe("masters");
    expect(brief.researchQuestion).toBe("");
    expect(brief.methodologies).toEqual([]);
    expect(brief.requirements.wordCount).toBeNull();
    expect(brief.requirements.allowedSourceTypes).toEqual([]);
  });
});

describe("discipline suggestions", () => {
  it("applies typical methods and style when asked", () => {
    const applied = applyDisciplineDefaults(briefFixture({ discipline: "law_and_policy", methodologies: [], citationStyle: "apa7", requirements: { ...createDefaultBrief().requirements } }), "law_and_policy");
    expect(applied.citationStyle).toBe("chicago");
    expect(applied.methodologies).toContain("doctrinal_legal");
    expect(applied.requirements.allowedSourceTypes).toContain("legislation");
  });

  it("keeps source types the student already chose", () => {
    const brief = briefFixture({ requirements: { ...briefFixture().requirements, allowedSourceTypes: ["journal_article"] } });
    const applied = applyDisciplineDefaults(brief, "law_and_policy");
    expect(applied.requirements.allowedSourceTypes).toEqual(["journal_article"]);
  });
});

describe("validation", () => {
  it("accepts a complete brief", () => {
    const issues = validateAcademicBrief(briefFixture());
    expect(hasBlockingIssue(issues)).toBe(false);
  });

  it("blocks on a missing or trivially short question", () => {
    expect(validateAcademicBrief(briefFixture({ researchQuestion: "   " })).some((issue) => issue.field === "researchQuestion" && issue.severity === "error")).toBe(true);
    expect(validateAcademicBrief(briefFixture({ researchQuestion: "why?" })).some((issue) => issue.messageKey === "error.question.tooShort")).toBe(true);
  });

  it("blocks when no methodology is selected", () => {
    const issues = validateAcademicBrief(briefFixture({ methodologies: [] }));
    expect(issues).toContainEqual(expect.objectContaining({ field: "methodologies", severity: "error", messageKey: "error.methodologies.required" }));
  });

  it("rejects a methodology outside the catalogue", () => {
    const issues = validateAcademicBrief(briefFixture({ methodologies: ["vibes" as never] }));
    expect(issues.some((issue) => issue.messageKey === "error.methodologies.unknown")).toBe(true);
  });

  it("rejects an unknown style or level rather than defaulting silently", () => {
    expect(validateAcademicBrief(briefFixture({ citationStyle: "mla8" as never })).some((issue) => issue.messageKey === "error.style.unknown")).toBe(true);
    expect(validateAcademicBrief(briefFixture({ level: "undergraduate" as never })).some((issue) => issue.messageKey === "error.level.unknown")).toBe(true);
  });

  it("warns instead of blocking when the word limit is not recorded", () => {
    const brief = briefFixture({ requirements: { ...briefFixture().requirements, wordCount: null } });
    const issues = validateAcademicBrief(brief);
    expect(issues).toContainEqual(expect.objectContaining({ field: "wordCount", severity: "warning", messageKey: "warning.wordCountMissing" }));
    expect(hasBlockingIssue(issues)).toBe(false);
  });

  it("rejects a peer-reviewed minimum larger than the total minimum", () => {
    const brief = briefFixture({ requirements: { ...briefFixture().requirements, minimumSources: 10, minimumPeerReviewed: 12 } });
    expect(validateAcademicBrief(brief)).toContainEqual(expect.objectContaining({ messageKey: "error.peerReviewed.exceedsTotal", severity: "error" }));
  });

  it("bounds total and peer-only targets by the studio's discovery limit", () => {
    for (const field of ["minimumSources", "minimumPeerReviewed"] as const) {
      const requirements = { ...briefFixture().requirements, minimumSources: null, minimumPeerReviewed: null };
      const atLimit = briefFixture({ requirements: { ...requirements, [field]: 120 } });
      const aboveLimit = briefFixture({ requirements: { ...requirements, [field]: 121 } });
      expect(hasBlockingIssue(validateAcademicBrief(atLimit))).toBe(false);
      expect(validateAcademicBrief(aboveLimit)).toContainEqual(
        expect.objectContaining({ field, severity: "error", messageKey: "error.sourceCount.range" }),
      );
    }
  });

  it("rejects out-of-range word counts and non-integers", () => {
    for (const wordCount of [12, briefLimits.wordCount.max + 1, 1500.5]) {
      expect(validateAcademicBrief(briefFixture({ requirements: { ...briefFixture().requirements, wordCount } })), String(wordCount)).toContainEqual(
        expect.objectContaining({ messageKey: "error.wordCount.range" }),
      );
    }
  });

  it("warns when the programme's allowed source types exclude journal articles", () => {
    const brief = briefFixture({ requirements: { ...briefFixture().requirements, allowedSourceTypes: ["book", "thesis"] } });
    expect(validateAcademicBrief(brief)).toContainEqual(expect.objectContaining({ messageKey: "warning.noJournalArticles", severity: "warning" }));
  });

  it("caps sub-questions and flags empty ones", () => {
    const tooMany = briefFixture({ reviewQuestions: ["a".repeat(20), "b".repeat(20), "c".repeat(20), "d".repeat(20), "e".repeat(20)] });
    expect(validateAcademicBrief(tooMany)).toContainEqual(expect.objectContaining({ messageKey: "error.reviewQuestions.max" }));
    expect(validateAcademicBrief(briefFixture({ reviewQuestions: [""] }))).toContainEqual(expect.objectContaining({ field: "reviewQuestion-0" }));
  });

  it("blocks a brief with too many keywords instead of silently dropping terms", () => {
    const keywords = Array.from({ length: briefLimits.maxKeywords + 1 }, (_, index) => `keyword-${index + 1}`);
    expect(parseKeywords(keywords.join(", "))).toEqual(keywords);
    expect(validateAcademicBrief(briefFixture({ keywords }))).toContainEqual(
      expect.objectContaining({ field: "keywords", severity: "error", messageKey: "error.keywords.max" }),
    );
  });

  it("blocks a keyword longer than the server-supported limit", () => {
    expect(validateAcademicBrief(briefFixture({ keywords: ["k".repeat(briefLimits.keyword.max + 1)] }))).toContainEqual(
      expect.objectContaining({ field: "keywords", severity: "error", messageKey: "error.keyword.tooLong" }),
    );
  });
});

describe("review summary", () => {
  it("lists the brief in order and omits what was not supplied", () => {
    const rows = summarizeBrief(briefFixture(), labels);
    expect(rows.map((row) => row.labelKey).slice(0, 5)).toEqual(["summary.level", "summary.discipline", "summary.question", "summary.methods", "summary.style"]);
    expect(rows.some((row) => row.labelKey === "summary.scope")).toBe(false);

    const full = summarizeBrief(briefFixture({ geographicScope: "Kurdistan Region of Iraq", keywords: ["pricing"], workingTitle: "Synthetic title" }), labels);
    expect(full.some((row) => row.labelKey === "summary.scope")).toBe(true);
    expect(full.find((row) => row.labelKey === "summary.wordCount")?.value).toBe("15,000");
  });

  it("states the full-text requirement in words rather than a tick", () => {
    const rows = summarizeBrief(briefFixture({ requirements: { ...briefFixture().requirements, requireFullText: true } }), labels);
    const row = rows.find((entry) => entry.labelKey === "summary.requireFullText");
    expect(row?.hintKey).toBe("summary.requireFullTextValue");
  });
});

describe("keyword parsing", () => {
  it("splits, trims and de-duplicates case-insensitively without truncating", () => {
    expect(parseKeywords(" pricing,  Pricing ;reimbursement\n , ")).toEqual(["pricing", "reimbursement"]);
    expect(parseKeywords(Array.from({ length: 20 }, (_, index) => `k${index}`).join(", "))).toHaveLength(20);
  });
});

describe("access rollup", () => {
  it("counts each access level independently", () => {
    const counts = countAccessLevels([
      { access: "full_text" },
      { access: "full_text" },
      { access: "abstract" },
      { access: "metadata" },
      { access: "unavailable" },
    ]);
    expect(counts).toEqual({ metadata: 1, abstract: 1, full_text: 2, unavailable: 1 });
  });
});
