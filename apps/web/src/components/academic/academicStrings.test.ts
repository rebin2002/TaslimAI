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

  // Scholarly record search is off in the shipped configuration default
  // (`AcademicSourceDiscovery.Enabled = false`), and the endpoint answers 503 while it is — and it
  // now answers 503 the same way when the switch is on but no catalog behind it can answer, because
  // both are the deployment's inability rather than an empty result. Only the variant shown after
  // the server actually answered a search may claim the capability is live; the pre-flight text
  // describes what the feature does, and the refusal text states that nothing is switched on.
  it("keeps each discovery notice honest about whether search is switched on", () => {
    expect(en("sources.discoveryBody")).not.toMatch(/is live|is enabled|for your workspace/i);
    expect(en("sources.discoveryBody")).toMatch(/scholarly record search returns/i);
    expect(en("sources.discoveryBodyLive")).toMatch(/is live for your workspace/i);
    expect(en("sources.discoveryOffTitle")).toMatch(/not turned on/i);
    // Phrased for the catalog layer, not only the master switch: "no catalog is switched on for it
    // yet" is true whether the operator left the master off or enabled a row without its key, and it
    // is never phrased as a result about the student's topic.
    expect(en("sources.discoveryOffBody")).toMatch(/no catalog is switched on for it yet/i);
    expect(en("sources.discoveryOffBody")).not.toMatch(/no records matched|nothing matched/i);
    // Whatever the deployment status, no notice may imply the works themselves were read or checked.
    for (const key of ["sources.discoveryBody", "sources.discoveryBodyLive", "sources.discoveryOffBody"] as const) {
      expect(en(key), key).not.toMatch(/we read the works|retraction checked|checks retractions for you/i);
    }
  });

  // The count line reports one bounded search, not the size of the literature, and the truncation
  // line must not quote a number: the ceiling that stopped a search may be the combined pool, one
  // catalog's own ceiling, or the whole-request budget, and only the first is known in the browser.
  it("describes counts and truncation as properties of this search", () => {
    expect(en("sources.findMatches")).toContain("surfaced by this search");
    expect(en("sources.findMatches")).not.toMatch(/available records|every record/i);
    expect(en("sources.findMore")).toMatch(/stopped at one of its own limits/i);
    expect(en("sources.findMore")).not.toMatch(/\d|bound is/i);
  });

  // Paging honesty. The panel holds no cursor and caches no pool, so every page request is a second,
  // independent search and the list it returns can differ: a record can appear again, or move. Copy
  // that implied the pages were slices of one snapshot would be promising an implementation this
  // feature does not have, so the caveat belongs in the sentence students read, not only in a
  // comment in the source.
  it("describes a page turn as another search rather than as a stable snapshot", () => {
    const limits = en("sources.findLimits");
    expect(limits).toMatch(/bounded number of records/i);
    expect(limits).toMatch(/searches the catalogs again/i);
    expect(limits).toMatch(/appear twice, or move/i);
    expect(limits).not.toMatch(/same (records|list) on every page|stable|identical|never (change|move)/i);
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
