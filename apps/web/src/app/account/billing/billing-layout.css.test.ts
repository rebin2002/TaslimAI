import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const billingStyles = readFileSync(new URL("./billing-layout.css", import.meta.url), "utf8");

/**
 * Collects every leaf rule selector (the block header directly above declarations)
 * together with the chain of enclosing at-rule headers, e.g. `@media (max-width: 900px)`.
 */
function collectLeafRules(css: string): { selector: string; atRules: string[] }[] {
  const withoutComments = css.replace(/\/\*[^]*?\*\//g, "");
  const rules: { selector: string; atRules: string[] }[] = [];
  const stack: { header: string; sawLeafBody: boolean }[] = [];
  let buffer = "";
  let directText = "";
  for (const char of withoutComments) {
    if (char === "{") {
      const header = buffer.trim();
      stack.push({ header, sawLeafBody: false });
      buffer = "";
      directText = "";
    } else if (char === "}") {
      const block = stack.pop();
      if (!block) continue;
      if (directText.trim() && !block.header.startsWith("@")) rules.push({ selector: block.header, atRules: stack.map((item) => item.header) });
      // A nested block means the parent was a wrapper (at-rule or grouping), not a leaf rule.
      if (stack.length && !block.header.startsWith("@")) stack[stack.length - 1].sawLeafBody = true;
      buffer = "";
      directText = "";
    } else if (char === ";") {
      if (stack.length) directText += buffer;
      buffer = "";
    } else {
      buffer += char;
    }
  }
  return rules;
}

describe("Billing route stylesheet scoping (PR #732)", () => {
  const leafRules = collectLeafRules(billingStyles);

  it("parses the expected number of leaf rules", () => {
    expect(leafRules.length).toBeGreaterThan(40);
  });

  it("scopes every rule to the billing page root so no selector can leak to other routes", () => {
    const unscoped = leafRules
      .map((rule) => rule.selector)
      .filter((selector) => !selector.includes(".billing-page"));
    expect(unscoped, `unscoped selectors: ${JSON.stringify(unscoped)}`).toEqual([]);
  });

  it("keeps at-rule wrappers limited to responsive and motion bands", () => {
    const atRules = [...new Set(leafRules.flatMap((rule) => rule.atRules))];
    expect(atRules.every((header) => header.startsWith("@media"))).toBe(true);
    expect(atRules).toContain("@media (max-width: 900px)");
    expect(atRules).toContain("@media (max-width: 680px)");
    expect(atRules).toContain("@media (prefers-reduced-motion: reduce)");
  });

  it("mirrors RTL inside the billing scope instead of globally", () => {
    const rtlSelectors = leafRules.filter((rule) => rule.selector.includes('[dir="rtl"]')).map((rule) => rule.selector);
    expect(rtlSelectors.length).toBeGreaterThan(0);
    for (const selector of rtlSelectors) {
      expect(selector).toContain(".billing-page");
    }
    expect(billingStyles).toContain('[dir="rtl"] .billing-page .billing-payment-status > strong {\n  justify-self: start;\n}');
  });

  it("keeps the ledger table as an intentional inner scroll region", () => {
    expect(billingStyles).toContain(".billing-page .usage-table-wrap {");
    expect(billingStyles).toContain("overflow-x: auto;");
    expect(billingStyles).toContain(".billing-page .billing-table {");
    expect(billingStyles).toContain("min-width: 680px;");
  });

  it("separates plan name, price and credit lines with block layout", () => {
    expect(billingStyles).toContain(".billing-page .billing-plan-option > b {\n  display: block;");
    expect(billingStyles).toContain(".billing-page .billing-plan-option > span:not(.billing-current-badge) {\n  display: block;");
    expect(billingStyles).toContain(".billing-page .billing-summary-grid .account-card > strong {\n  display: block;");
  });

  it("visually marks disabled checkout controls without changing behavior", () => {
    expect(billingStyles).toContain(".billing-page button:disabled {\n  cursor: not-allowed;");
  });
});
