/**
 * Issue #1259 (Academic Research Studio) — Phase A brief model.
 *
 * Pure, provider-free domain module: option catalogues, defaults, validation
 * and the review summary rendered by the Plan tab. No fetching, no generation
 * and no cost metadata, so it is deterministic under test and safe to render
 * server-side.
 *
 * Discipline option sets describe *common practice in the field* and are
 * presented as suggestions. A student's actual institutional rules belong to
 * the university requirements, which this module never invents.
 */
import type { AccessLevel, AcademicSourceType, CitationStyleId } from "./academicSourceLibrary";

export type AcademicMode = "standard" | "academic";
export type AcademicLevel = "masters" | "doctoral";

export type Methodology =
  | "systematic_review"
  | "narrative_review"
  | "scoping_review"
  | "qualitative"
  | "quantitative"
  | "mixed_methods"
  | "case_study"
  | "comparative"
  | "doctrinal_legal"
  | "secondary_desktop";

export type DisciplineId =
  | "medicine_and_health"
  | "pharmacy_and_pharmacotherapy"
  | "public_health"
  | "nursing"
  | "psychology"
  | "education"
  | "business_and_management"
  | "economics_and_finance"
  | "law_and_policy"
  | "computer_science_and_information_systems"
  | "engineering"
  | "natural_sciences"
  | "social_sciences"
  | "arts_and_humanities"
  | "other";

export type Discipline = {
  id: DisciplineId;
  labelKey: string;
  /** Commonly used methods in the field; offered as suggestions. */
  suggestedMethodologies: readonly Methodology[];
  /** A style commonly expected in the field; always overridable. */
  suggestedCitationStyle: CitationStyleId;
  /** Source types a reviewer in the field normally expects to see. */
  suggestedSourceTypes: readonly AcademicSourceType[];
  /** Whether monographs and theses carry the argument, not just websites. */
  booksAreCentral: boolean;
};

export const academicLevels: readonly AcademicLevel[] = ["masters", "doctoral"];

export const citationStyles: readonly CitationStyleId[] = ["apa7", "harvard", "chicago", "ieee", "vancouver"];

export const methodologyOptions: readonly Methodology[] = [
  "systematic_review",
  "narrative_review",
  "scoping_review",
  "qualitative",
  "quantitative",
  "mixed_methods",
  "case_study",
  "comparative",
  "doctrinal_legal",
  "secondary_desktop",
];

const scholarlySourceTypes: readonly AcademicSourceType[] = [
  "journal_article",
  "book",
  "book_chapter",
  "edited_volume",
  "conference_paper",
  "thesis",
  "report",
  "working_paper",
  "standard_or_guideline",
  "dataset",
];

export const disciplines: readonly Discipline[] = [
  {
    id: "medicine_and_health",
    labelKey: "discipline.medicine",
    suggestedMethodologies: ["systematic_review", "quantitative", "mixed_methods", "qualitative"],
    suggestedCitationStyle: "vancouver",
    suggestedSourceTypes: ["journal_article", "report", "standard_or_guideline", "thesis", "dataset"],
    booksAreCentral: false,
  },
  {
    id: "pharmacy_and_pharmacotherapy",
    labelKey: "discipline.pharmacy",
    suggestedMethodologies: ["systematic_review", "quantitative", "comparative", "secondary_desktop"],
    suggestedCitationStyle: "vancouver",
    suggestedSourceTypes: ["journal_article", "report", "standard_or_guideline", "legislation", "dataset"],
    booksAreCentral: false,
  },
  {
    id: "public_health",
    labelKey: "discipline.publicHealth",
    suggestedMethodologies: ["systematic_review", "mixed_methods", "quantitative", "qualitative"],
    suggestedCitationStyle: "apa7",
    suggestedSourceTypes: ["journal_article", "report", "dataset", "thesis"],
    booksAreCentral: false,
  },
  {
    id: "nursing",
    labelKey: "discipline.nursing",
    suggestedMethodologies: ["systematic_review", "qualitative", "mixed_methods"],
    suggestedCitationStyle: "apa7",
    suggestedSourceTypes: ["journal_article", "thesis", "report"],
    booksAreCentral: false,
  },
  {
    id: "psychology",
    labelKey: "discipline.psychology",
    suggestedMethodologies: ["quantitative", "qualitative", "mixed_methods", "case_study"],
    suggestedCitationStyle: "apa7",
    suggestedSourceTypes: ["journal_article", "book", "book_chapter", "thesis"],
    booksAreCentral: true,
  },
  {
    id: "education",
    labelKey: "discipline.education",
    suggestedMethodologies: ["mixed_methods", "qualitative", "case_study", "systematic_review"],
    suggestedCitationStyle: "apa7",
    suggestedSourceTypes: ["journal_article", "book", "book_chapter", "thesis", "report"],
    booksAreCentral: true,
  },
  {
    id: "business_and_management",
    labelKey: "discipline.business",
    suggestedMethodologies: ["case_study", "qualitative", "mixed_methods", "secondary_desktop"],
    suggestedCitationStyle: "harvard",
    suggestedSourceTypes: ["journal_article", "book", "report", "conference_paper", "thesis"],
    booksAreCentral: true,
  },
  {
    id: "economics_and_finance",
    labelKey: "discipline.economics",
    suggestedMethodologies: ["quantitative", "secondary_desktop", "comparative"],
    suggestedCitationStyle: "harvard",
    suggestedSourceTypes: ["journal_article", "book", "report", "dataset", "working_paper"],
    booksAreCentral: true,
  },
  {
    id: "law_and_policy",
    labelKey: "discipline.law",
    suggestedMethodologies: ["doctrinal_legal", "comparative", "qualitative", "case_study"],
    suggestedCitationStyle: "chicago",
    suggestedSourceTypes: ["book", "book_chapter", "journal_article", "legislation", "report"],
    booksAreCentral: true,
  },
  {
    id: "computer_science_and_information_systems",
    labelKey: "discipline.computing",
    suggestedMethodologies: ["quantitative", "mixed_methods", "secondary_desktop", "case_study"],
    suggestedCitationStyle: "ieee",
    suggestedSourceTypes: ["conference_paper", "journal_article", "dataset", "thesis", "report"],
    booksAreCentral: false,
  },
  {
    id: "engineering",
    labelKey: "discipline.engineering",
    suggestedMethodologies: ["quantitative", "case_study", "comparative"],
    suggestedCitationStyle: "ieee",
    suggestedSourceTypes: ["conference_paper", "journal_article", "standard_or_guideline", "report"],
    booksAreCentral: false,
  },
  {
    id: "natural_sciences",
    labelKey: "discipline.naturalSciences",
    suggestedMethodologies: ["quantitative", "systematic_review", "comparative"],
    suggestedCitationStyle: "vancouver",
    suggestedSourceTypes: ["journal_article", "book", "dataset", "thesis"],
    booksAreCentral: false,
  },
  {
    id: "social_sciences",
    labelKey: "discipline.socialSciences",
    suggestedMethodologies: ["qualitative", "mixed_methods", "comparative", "case_study"],
    suggestedCitationStyle: "harvard",
    suggestedSourceTypes: ["journal_article", "book", "book_chapter", "thesis", "report"],
    booksAreCentral: true,
  },
  {
    id: "arts_and_humanities",
    labelKey: "discipline.humanities",
    suggestedMethodologies: ["qualitative", "comparative", "case_study", "narrative_review"],
    suggestedCitationStyle: "chicago",
    suggestedSourceTypes: ["book", "book_chapter", "journal_article", "edited_volume", "thesis"],
    booksAreCentral: true,
  },
  {
    id: "other",
    labelKey: "discipline.other",
    suggestedMethodologies: ["narrative_review", "qualitative", "quantitative", "mixed_methods"],
    suggestedCitationStyle: "apa7",
    suggestedSourceTypes: scholarlySourceTypes,
    booksAreCentral: false,
  },
];

export function findDiscipline(id: DisciplineId | string): Discipline {
  return disciplines.find((entry) => entry.id === id) ?? disciplines[disciplines.length - 1];
}

export function isDisciplineId(value: string): value is DisciplineId {
  return disciplines.some((entry) => entry.id === value);
}

export type UniversityRequirements = {
  /** Institutional word limit, as stated by the programme. */
  wordCount: number | null;
  /** Minimum source count required by the programme. */
  minimumSources: number | null;
  /** Minimum peer-reviewed source count required by the programme. */
  minimumPeerReviewed: number | null;
  /** Source types the programme permits; empty means "not specified". */
  allowedSourceTypes: AcademicSourceType[];
  /** When set, only works the student can read in full may be cited. */
  requireFullText: boolean;
  /** Rules copied from the handbook, so they are the student's, not ours. */
  referencingGuideNotes: string;
};

export type AcademicBrief = {
  mode: AcademicMode;
  level: AcademicLevel;
  discipline: DisciplineId;
  citationStyle: CitationStyleId;
  methodologies: Methodology[];
  researchQuestion: string;
  workingTitle: string;
  /** Sub-questions the evidence matrix is built around. */
  reviewQuestions: string[];
  keywords: string[];
  geographicScope: string;
  populationOrContext: string;
  timePeriod: string;
  requirements: UniversityRequirements;
};

export const briefLimits = {
  researchQuestion: { min: 12, max: 1500 },
  workingTitle: { max: 200 },
  geographicScope: { max: 160 },
  populationOrContext: { max: 240 },
  timePeriod: { max: 120 },
  referencingGuideNotes: { max: 2000 },
  reviewQuestion: { min: 6, max: 300 },
  maxReviewQuestions: 4,
  maxKeywords: 12,
  wordCount: { min: 1000, max: 200000 },
  sourceCount: { min: 0, max: 1000 },
} as const;

export function createDefaultBrief(level: AcademicLevel = "masters"): AcademicBrief {
  return {
    mode: "academic",
    level,
    discipline: "other",
    citationStyle: "apa7",
    methodologies: [],
    researchQuestion: "",
    workingTitle: "",
    reviewQuestions: [],
    keywords: [],
    geographicScope: "",
    populationOrContext: "",
    timePeriod: "",
    requirements: {
      wordCount: null,
      minimumSources: null,
      minimumPeerReviewed: null,
      allowedSourceTypes: [],
      requireFullText: false,
      referencingGuideNotes: "",
    },
  };
}

/**
 * Applies a discipline's common practice as a starting suggestion. Called only
 * from an explicit user action, so switching discipline never silently
 * rewrites answers the student already gave.
 */
export function applyDisciplineDefaults(brief: AcademicBrief, disciplineId: DisciplineId): AcademicBrief {
  const discipline = findDiscipline(disciplineId);
  return {
    ...brief,
    discipline: discipline.id,
    citationStyle: discipline.suggestedCitationStyle,
    methodologies: [...discipline.suggestedMethodologies],
    requirements: {
      ...brief.requirements,
      allowedSourceTypes: brief.requirements.allowedSourceTypes.length > 0
        ? brief.requirements.allowedSourceTypes
        : [...discipline.suggestedSourceTypes],
    },
  };
}

export type BriefIssueSeverity = "error" | "warning";

export type BriefIssue = {
  /** Stable id of the control the issue belongs to, used for aria-describedby. */
  field: string;
  severity: BriefIssueSeverity;
  messageKey: string;
  params?: Record<string, string>;
};

function textLength(value: string): number {
  return value.trim().length;
}

/**
 * Blocks progress only on things that make a plan unusable or unsafe. Warnings
 * cover advice the student can override — a programme's own rules always win.
 */
export function validateAcademicBrief(brief: AcademicBrief): BriefIssue[] {
  const issues: BriefIssue[] = [];
  const question = brief.researchQuestion.trim();

  if (textLength(question) === 0) {
    issues.push({ field: "researchQuestion", severity: "error", messageKey: "error.question.required" });
  } else if (question.length < briefLimits.researchQuestion.min) {
    issues.push({
      field: "researchQuestion",
      severity: "error",
      messageKey: "error.question.tooShort",
      params: { min: String(briefLimits.researchQuestion.min) },
    });
  } else if (question.length > briefLimits.researchQuestion.max) {
    issues.push({
      field: "researchQuestion",
      severity: "error",
      messageKey: "error.question.tooLong",
      params: { max: String(briefLimits.researchQuestion.max) },
    });
  }

  if (!isDisciplineId(brief.discipline)) {
    issues.push({ field: "discipline", severity: "error", messageKey: "error.discipline.unknown" });
  }
  if (!academicLevels.includes(brief.level)) {
    issues.push({ field: "level", severity: "error", messageKey: "error.level.unknown" });
  }
  if (!citationStyles.includes(brief.citationStyle)) {
    issues.push({ field: "citationStyle", severity: "error", messageKey: "error.style.unknown" });
  }
  if (brief.methodologies.length === 0) {
    issues.push({ field: "methodologies", severity: "error", messageKey: "error.methodologies.required" });
  } else if (brief.methodologies.some((entry) => !methodologyOptions.includes(entry))) {
    issues.push({ field: "methodologies", severity: "error", messageKey: "error.methodologies.unknown" });
  }

  if (brief.workingTitle.length > briefLimits.workingTitle.max) {
    issues.push({
      field: "workingTitle",
      severity: "error",
      messageKey: "error.title.tooLong",
      params: { max: String(briefLimits.workingTitle.max) },
    });
  }

  if (brief.reviewQuestions.length > briefLimits.maxReviewQuestions) {
    issues.push({
      field: "reviewQuestions",
      severity: "error",
      messageKey: "error.reviewQuestions.max",
      params: { max: String(briefLimits.maxReviewQuestions) },
    });
  }
  brief.reviewQuestions.forEach((entry, index) => {
    if (textLength(entry) < briefLimits.reviewQuestion.min) {
      issues.push({ field: `reviewQuestion-${index}`, severity: "error", messageKey: "error.reviewQuestion.short" });
    }
  });

  if (brief.keywords.length > briefLimits.maxKeywords) {
    issues.push({
      field: "keywords",
      severity: "warning",
      messageKey: "error.keywords.max",
      params: { max: String(briefLimits.maxKeywords) },
    });
  }

  const { wordCount, minimumSources, minimumPeerReviewed, allowedSourceTypes } = brief.requirements;
  if (wordCount !== null) {
    if (!Number.isInteger(wordCount) || wordCount < briefLimits.wordCount.min || wordCount > briefLimits.wordCount.max) {
      issues.push({
        field: "wordCount",
        severity: "error",
        messageKey: "error.wordCount.range",
        params: { min: String(briefLimits.wordCount.min), max: String(briefLimits.wordCount.max) },
      });
    }
  }
  for (const [field, value] of [["minimumSources", minimumSources], ["minimumPeerReviewed", minimumPeerReviewed]] as const) {
    if (value === null) continue;
    if (!Number.isInteger(value) || value < briefLimits.sourceCount.min || value > briefLimits.sourceCount.max) {
      issues.push({ field, severity: "error", messageKey: "error.sourceCount.range" });
    }
  }
  if (minimumSources !== null && minimumPeerReviewed !== null && minimumPeerReviewed > minimumSources) {
    issues.push({ field: "minimumPeerReviewed", severity: "error", messageKey: "error.peerReviewed.exceedsTotal" });
  }
  if (allowedSourceTypes.length > 0 && textLength(question) > 0 && !allowedSourceTypes.includes("journal_article")) {
    issues.push({ field: "allowedSourceTypes", severity: "warning", messageKey: "warning.noJournalArticles" });
  }
  if (textLength(question) > 0 && brief.requirements.wordCount === null) {
    issues.push({ field: "wordCount", severity: "warning", messageKey: "warning.wordCountMissing" });
  }
  if (brief.methodologies.includes("systematic_review") && brief.reviewQuestions.length === 0) {
    issues.push({ field: "reviewQuestions", severity: "warning", messageKey: "warning.reviewQuestionsForSystematic" });
  }

  return issues;
}

export function hasBlockingIssue(issues: readonly BriefIssue[]): boolean {
  return issues.some((issue) => issue.severity === "error");
}

export function issuesForField(issues: readonly BriefIssue[], field: string): BriefIssue[] {
  return issues.filter((issue) => issue.field === field);
}

/** Ordered review rows for the Plan tab; empty optional fields are omitted. */
export type BriefSummaryRow = { labelKey: string; value: string; hintKey?: string };

export function summarizeBrief(brief: AcademicBrief, labels: {
  discipline: (id: DisciplineId) => string;
  level: (level: AcademicLevel) => string;
  style: (style: CitationStyleId) => string;
  methodology: (methodology: Methodology) => string;
  sourceType: (type: AcademicSourceType) => string;
}): BriefSummaryRow[] {
  const rows: BriefSummaryRow[] = [
    { labelKey: "summary.level", value: labels.level(brief.level) },
    { labelKey: "summary.discipline", value: labels.discipline(brief.discipline) },
    { labelKey: "summary.question", value: brief.researchQuestion.trim() || "—" },
    { labelKey: "summary.methods", value: brief.methodologies.map(labels.methodology).join(", ") || "—" },
    { labelKey: "summary.style", value: labels.style(brief.citationStyle) },
  ];
  if (brief.workingTitle.trim()) rows.push({ labelKey: "summary.workingTitle", value: brief.workingTitle.trim() });
  if (brief.reviewQuestions.length) rows.push({ labelKey: "summary.reviewQuestions", value: brief.reviewQuestions.filter((q) => q.trim()).join(" · ") });
  if (brief.keywords.length) rows.push({ labelKey: "summary.keywords", value: brief.keywords.join(", ") });
  if (brief.geographicScope.trim()) rows.push({ labelKey: "summary.scope", value: brief.geographicScope.trim() });
  if (brief.populationOrContext.trim()) rows.push({ labelKey: "summary.population", value: brief.populationOrContext.trim() });
  if (brief.timePeriod.trim()) rows.push({ labelKey: "summary.period", value: brief.timePeriod.trim() });
  const { wordCount, minimumSources, minimumPeerReviewed, allowedSourceTypes, requireFullText } = brief.requirements;
  if (wordCount !== null) rows.push({ labelKey: "summary.wordCount", value: wordCount.toLocaleString("en-US") });
  if (minimumSources !== null) rows.push({ labelKey: "summary.minimumSources", value: String(minimumSources) });
  if (minimumPeerReviewed !== null) rows.push({ labelKey: "summary.minimumPeerReviewed", value: String(minimumPeerReviewed) });
  if (allowedSourceTypes.length) rows.push({ labelKey: "summary.allowedTypes", value: allowedSourceTypes.map(labels.sourceType).join(", ") });
  if (requireFullText) rows.push({ labelKey: "summary.requireFullText", value: "—", hintKey: "summary.requireFullTextValue" });
  if (brief.requirements.referencingGuideNotes.trim()) {
    rows.push({ labelKey: "summary.guideNotes", value: brief.requirements.referencingGuideNotes.trim() });
  }
  return rows;
}

/** Access-level rollup used by the Plan and Sources tabs. */
export function accessRollup(counts: Record<AccessLevel, number>): { readable: number; unreadable: number } {
  return {
    readable: counts.full_text ?? 0,
    unreadable: (counts.metadata ?? 0) + (counts.abstract ?? 0) + (counts.unavailable ?? 0),
  };
}

export function countAccessLevels(sources: readonly { access: AccessLevel }[]): Record<AccessLevel, number> {
  const counts: Record<AccessLevel, number> = { metadata: 0, abstract: 0, full_text: 0, unavailable: 0 };
  for (const source of sources) counts[source.access] += 1;
  return counts;
}

/** Keyword parsing: comma or newline separated, de-duplicated, order kept. */
export function parseKeywords(raw: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const part of raw.split(/[,\n;]/)) {
    const value = part.trim();
    if (!value) continue;
    const key = value.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(value);
  }
  return out.slice(0, briefLimits.maxKeywords);
}
