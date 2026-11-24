import { describe, expect, it } from "vitest";
import {
  academicExportCopy,
  academicExportCopyLocales,
  missingExportCopyKeys,
  type AcademicExportCopyKey,
} from "./academicExportCopy";

const copyKeys = [
  "panelTitle",
  "panelIntro",
  "reportLabel",
  "reportPlaceholder",
  "reportsLoading",
  "reportsEmpty",
  "reportsFailed",
  "summaryLoading",
  "summaryTitle",
  "summaryFailed",
  "savedAt",
  "sectionsCount",
  "sourcesCount",
  "accessedCount",
  "metadataOnlyCount",
  "metadataWarning",
  "disclosureHeading",
  "disclosureBody",
  "disclosureLabel",
  "disclosurePlaceholder",
  "disclosureMissing",
  "templateHeading",
  "templateSelected",
  "templateNone",
  "formatHeading",
  "exportDocx",
  "exportPdf",
  "exporting",
  "statusFirst",
  "statusSame",
  "statusReformat",
  "downloadCheck",
  "errorGeneric",
  "errorIntegrity",
  "errorDirection",
  "errorNotFound",
  "errorRateLimited",
] as AcademicExportCopyKey[];

describe("academicExportCopy", () => {
  it("ships every key in every locale", () => {
    for (const locale of academicExportCopyLocales) {
      expect(missingExportCopyKeys(locale)).toEqual([]);
      for (const key of copyKeys) {
        expect(academicExportCopy(locale, key).trim().length).toBeGreaterThan(0);
      }
    }
  });

  it("never mentions vendors, infrastructure or billing vocabulary", () => {
    const forbidden = [
      "openai",
      "anthropic",
      "gemini",
      "gpt",
      "claude",
      "api key",
      "provider",
      "endpoint",
      "routing",
      "credit",
      "usd",
      "billing",
      "invoice",
      " €",
      "$",
    ];
    for (const locale of academicExportCopyLocales) {
      for (const key of copyKeys) {
        const text = academicExportCopy(locale, key).toLowerCase();
        for (const word of forbidden) {
          expect(text, `${locale}/${key} contains ${word}`).not.toContain(word);
        }
        expect(text).not.toMatch(/\bmodel\b|\bcost\b|\bprice\b|\btoken\b/);
      }
    }
  });

  it("promises no writing and no new research, in the English surface", () => {
    expect(academicExportCopy("en", "panelIntro")).toContain("no new research is started");
    expect(academicExportCopy("en", "statusFirst")).toContain("No new research was started");
    for (const locale of academicExportCopyLocales) {
      for (const key of copyKeys) {
        expect(academicExportCopy(locale, key).toLowerCase()).not.toMatch(/write (my|your) thesis|writes your report|submits? (your|the) (thesis|paper)/);
      }
    }
  });

  it("states the two honesty rules: metadata-only sources and the permanent disclosure", () => {
    expect(academicExportCopy("en", "metadataWarning")).toContain("were not read in full");
    expect(academicExportCopy("en", "disclosureBody")).toContain("cannot be removed");
    expect(academicExportCopy("en", "disclosureMissing")).toContain("incomplete");
    // The bounded-render refusal must tell the truth too: nothing was produced, nothing was started.
    expect(academicExportCopy("en", "errorRateLimited")).toContain("was not produced");
    expect(academicExportCopy("en", "errorRateLimited")).toContain("no new research was started");
    // The same rules must be present in the non-English packs, not silently dropped.
    expect(academicExportCopy("ar", "disclosureBody")).toContain("لا يمكن حذفه");
    expect(academicExportCopy("ckb", "disclosureBody")).toContain("ناڕەشێت بدرێت");
  });

  it("interpolates counts and dates without leaving braces", () => {
    expect(academicExportCopy("en", "panelIntro", { count: 230 })).toContain("230 layout designs");
    expect(academicExportCopy("ar", "sourcesCount", { count: 12 })).toContain("12");
    expect(academicExportCopy("ckb", "savedAt", { date: "٥" })).not.toContain("{");
  });

  it("falls back to English for an unknown locale instead of rendering a key", () => {
    expect(academicExportCopy("xx" as never, "panelTitle")).toBe(academicExportCopy("en", "panelTitle"));
  });
});
