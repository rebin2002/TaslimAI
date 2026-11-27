/**
 * Issue #1259 (Academic Research Studio) — tab semantics and field plumbing.
 *
 * Rendered with `react-dom/server` in the repo's default Node environment, so
 * no browser dependency is added. The interactive contract that matters for
 * assistive technology — roles, selection, ownership and description ids — is
 * fully observable in the static markup.
 */
import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { AcademicTabs } from "./AcademicTabs";
import { AcademicField, fieldDescribedBy, fieldHasError, fieldHintId } from "./AcademicPrimitives";
import { createAcademicTranslator } from "./academicStrings";
import type { AcademicTab } from "./academicStudioState";

const t = createAcademicTranslator("en");
const labels: Record<AcademicTab, string> = { plan: "Plan", sources: "Sources", evidence: "Evidence", chapters: "Chapters" };

function renderTabShell(active: AcademicTab, idPrefix = "shell") {
  return renderToStaticMarkup(
    <AcademicTabs tab={active} onSelect={() => undefined} t={t} labels={labels} counts={{ sources: "4" }} renderPanel={(tab) => <p data-panel={tab}>panel body</p>} idPrefix={idPrefix} />,
  );
}

describe("AcademicTabs", () => {
  const html = renderTabShell("plan");

  it("exposes one tablist with the four named tabs", () => {
    expect(html).toContain('role="tablist"');
    expect(html.match(/role="tab"/g)).toHaveLength(4);
    for (const label of Object.values(labels)) expect(html).toContain(label);
    expect(html).toContain('aria-label="Academic workspace sections"');
  });

  it("marks selection and ownership for every tab/panel pair", () => {
    for (const tab of ["plan", "sources", "evidence", "chapters"]) {
      expect(html).toContain(`id="shell-tab-${tab}"`);
      expect(html).toContain(`aria-controls="shell-panel-${tab}"`);
    }
    expect(html).toContain('id="shell-panel-plan"');
    expect(html).toContain('aria-labelledby="shell-tab-plan"');
    expect(html).toContain('aria-selected="true"');
    expect((html.match(/aria-selected="true"/g) ?? []).length).toBe(1);
    expect((html.match(/aria-selected="false"/g) ?? []).length).toBe(3);
  });

  it("keeps exactly one tab in the sequential tab order", () => {
    expect((html.match(/tabindex="0"/g) ?? [])).toHaveLength(2); // one tab plus its panel
    expect((html.match(/tabindex="-1"/g) ?? [])).toHaveLength(3);
  });

  it("renders only the selected panel", () => {
    expect(html).toContain('data-panel="plan"');
    expect(html).not.toContain('data-panel="evidence"');
  });

  it("shows the source count on its tab", () => {
    expect(html).toContain(">4<");
  });

  it("does not present a tab as a button that starts work", () => {
    // Tabs are `role="tab"`, and no form submit control exists in the strip.
    expect(html).not.toContain('type="submit"');
    expect(html).toContain('type="button"');
  });

  it("uses instance-scoped ids so two shells never collide", () => {
    const other = renderTabShell("chapters", "other");
    expect(other).toContain('id="other-tab-chapters"');
    expect(other).not.toContain('id="shell-tab-chapters"');
    expect(other).toContain('aria-selected="true"');
  });
});

describe("AcademicField", () => {
  it("associates the label with the control it wraps", () => {
    const html = renderToStaticMarkup(
      <AcademicField controlId="ctl-question" fieldName="researchQuestion" label="Research question" hint="In your own words." required t={t}>
        <textarea id="ctl-question" />
      </AcademicField>,
    );
    expect(html).toContain('<label class="');
    expect(html).toContain('for="ctl-question"');
    expect(html).toContain('id="ctl-question-hint"');
    expect(html).toContain("In your own words.");
    expect(html).toContain('data-academic-field="researchQuestion"');
  });

  it("renders a fieldset with a legend for option groups", () => {
    const html = renderToStaticMarkup(
      <AcademicField controlId="ctl-methods" fieldName="methodologies" label="Methodology" hint="Pick one." as="group" t={t}>
        <div role="group" />
      </AcademicField>,
    );
    expect(html).toContain("<fieldset");
    expect(html).toContain("<legend");
    expect(html).toContain("Methodology");
  });

  it("announces an error message with the id the control references", () => {
    const issues = [{ field: "wordCount", severity: "error" as const, messageKey: "error.wordCount.range", params: { min: "1000", max: "200000" } }];
    const html = renderToStaticMarkup(
      <AcademicField controlId="ctl-words" fieldName="wordCount" label="Word limit" issues={issues} t={t}>
        <input id="ctl-words" aria-describedby={fieldDescribedBy("ctl-words", { issues })} aria-invalid={fieldHasError(issues)} />
      </AcademicField>,
    );
    expect(html).toContain('role="alert"');
    expect(html).toContain('id="ctl-words-error-0"');
    expect(html).toContain("between 1000 and 200000");
    expect(html).toContain('aria-invalid="true"');
  });
});

describe("description id helpers", () => {
  it("orders hint before messages and returns nothing when there is nothing to describe", () => {
    expect(fieldDescribedBy("x")).toBeUndefined();
    expect(fieldDescribedBy("x", { hint: true })).toBe(fieldHintId("x"));
    const issues = [
      { field: "x", severity: "error" as const, messageKey: "error.question.required" },
      { field: "x", severity: "warning" as const, messageKey: "warning.wordCountMissing" },
    ];
    expect(fieldDescribedBy("x", { hint: true, issues })).toBe(`${fieldHintId("x")} x-error-0 x-error-1`);
  });

  it("only treats error severity as invalid", () => {
    expect(fieldHasError([{ field: "x", severity: "warning", messageKey: "warning.wordCountMissing" }])).toBe(false);
    expect(fieldHasError([{ field: "x", severity: "error", messageKey: "error.question.required" }])).toBe(true);
  });
});
