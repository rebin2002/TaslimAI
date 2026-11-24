/**
 * Issue #1259 (Academic Research Studio) — copy integrity tests.
 *
 * The issue sets three customer-facing rules: never disclose provider or model
 * names, never surface internal cost, and never market the product as automated
 * thesis writing. Those are prose rules, so they are enforced as prose tests over
 * the whole dictionary rather than left to review.
 */
import { describe, expect, it } from "vitest";
import { academicLocaleCoverage, academicStringKeys, createAcademicLabels, createAcademicTranslator } from "./academicStrings";
import { academicCapabilities, academicCapabilityIds, academicWorkflowStages } from "./academicCapabilities";
import { createDefaultBrief, disciplines, methodologyOptions, citationStyles } from "./academicBrief";
import { accessLevels, academicSourceTypes } from "./academicSourceLibrary";

const en = createAcademicTranslator("en");

/** Vendor, model and infrastructure names that must never reach a student. */
const forbiddenNames = [
  "openalex",
  "crossref",
  "open library",
  "google books",
  "pubmed",
  "europe pmc",
  "semanticscholar",
  "semantic scholar",
  "jstor",
  "elsevier",
  "springer",
  "wiley",
  "taylor & francis",
  "gpt",
  "claude",
  "gemini",
  "llama",
  "mistral",
  "grok",
  "deepseek",
  "qwen",
  "azure",
  "openai",
  "anthropic",
  "bedrock",
  "provider",
  "model name",
  "api key",
];

/** Internal commercial wording that must stay out of the customer surface. */
const forbiddenCostWords = ["credit", "$", "usd", " price", "pricing per", "token", "billing", "recharge", "cost of"];

/** Marketing claims the issue forbids. */
const forbiddenClaims = ["write my thesis", "writes your thesis", "automated thesis writing", "guaranteed pass", "ghostwrit"];

function allKeys(): string[] {
  const briefKeys = Object.keys(createDefaultBrief());
  return [
    ...academicStringKeys(),
    ...academicCapabilityIds.map((id) => academicCapabilities[id].detailKey),
    ...academicWorkflowStages.flatMap((stage) => [stage.labelKey, stage.descriptionKey]),
    ...disciplines.map((discipline) => discipline.labelKey),
    ...methodologyOptions.map((methodology) => `methodology.${methodology}`),
    ...citationStyles.map((style) => `style.${style}`),
    ...academicSourceTypes.map((type) => `sourceType.${type}`),
    ...accessLevels.map((level) => `access.${level}`),
    ...briefKeys.map((key) => `field.${key}`),
  ];
}

describe("dictionary", () => {
  it("resolves every key to real copy instead of the key itself", () => {
    for (const key of academicStringKeys()) {
      const value = en(key as never);
      expect(value, key).not.toBe(key);
      expect(value.trim().length, key).toBeGreaterThan(1);
    }
  });

  it("answers every derived key the domain modules can emit", () => {
    const missing = allKeys().filter((key) => en(key as never) === key);
    // `field.<briefKey>` is intentionally not part of the dictionary: brief fields
    // use their own descriptive labels, so filter that probe out.
    const realGaps = missing.filter((key) => !key.startsWith("field."));
    expect(realGaps).toEqual([]);
  });

  it("interpolates named placeholders", () => {
    expect(en("field.reviewQuestionN", { n: "3" })).toBe("Sub-question 3");
    expect(en("sources.count", { visible: "2", total: "5" })).toBe("2 shown of 5");
    expect(en("plan.blockedCount", { count: "4" })).toContain("4");
  });

  it("falls back to English for a missing translation rather than showing a key", () => {
    const ar = createAcademicTranslator("ar");
    expect(ar("stage.verify.label")).not.toBe("stage.verify.label");
    const ku = createAcademicTranslator("ku");
    expect(ku("plan.readiness")).not.toBe("plan.readiness");
    expect(createAcademicTranslator("zz")("plan.readiness")).toBe(en("plan.readiness"));
  });

  it("keeps Arabic and Kurdish coverage of the interactive labels", () => {
    const total = academicLocaleCoverage("en");
    expect(academicLocaleCoverage("ar")).toBeGreaterThan(total * 0.35);
    expect(academicLocaleCoverage("ku")).toBeGreaterThan(total * 0.35);
  });
});

describe("customer-facing copy rules", () => {
  it("never names a provider, model or infrastructure vendor", () => {
    for (const key of academicStringKeys()) {
      const value = en(key as never).toLowerCase();
      for (const name of forbiddenNames) {
        expect(value, `${key} mentions ${name}`).not.toContain(name);
      }
    }
  });

  it("never surfaces internal cost, credits or metering amounts", () => {
    for (const key of academicStringKeys()) {
      const value = en(key as never).toLowerCase();
      for (const word of forbiddenCostWords) {
        expect(value, `${key} mentions ${word}`).not.toContain(word);
      }
    }
  });

  it("never claims to write the thesis for the student", () => {
    for (const key of academicStringKeys()) {
      const value = en(key as never).toLowerCase();
      for (const claim of forbiddenClaims) {
        expect(value, `${key} claims ${claim}`).not.toContain(claim);
      }
    }
  });

  it("describes the budget step as an approval, not an amount", () => {
    expect(en("capability.planned.budget")).toMatch(/approval/i);
    expect(en("capability.planned.budget")).not.toMatch(/\d/);
  });
});

describe("option labelers", () => {
  it("resolve every catalogue option to copy", () => {
    const labels = createAcademicLabels(en);
    for (const discipline of disciplines) expect(labels.discipline(discipline.id)).not.toMatch(/^discipline\./);
    for (const methodology of methodologyOptions) expect(labels.methodology(methodology)).not.toBe(`methodology.${methodology}`);
    for (const style of citationStyles) expect(labels.style(style)).not.toBe(`style.${style}`);
    for (const type of academicSourceTypes) expect(labels.sourceType(type)).not.toBe(`sourceType.${type}`);
    for (const level of accessLevels) expect(labels.access(level)).not.toBe(`access.${level}`);
    expect(labels.level("doctoral")).toBe("Doctoral");
  });
});
