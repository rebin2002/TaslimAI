import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./BillingView.tsx", import.meta.url), "utf8");

describe("Billing status accessibility", () => {
  it("announces billing loading as a polite status", () => {
    expect(source).toMatch(/className="account-card usage-loading" role="status" aria-live="polite"/);
  });

  it("announces billing load failures as assertive alerts", () => {
    expect(source).toMatch(/className="form-error" role="alert" aria-live="assertive"/);
  });

  it("announces payment failure reasons as alerts", () => {
    expect(source).toMatch(/className="form-error" role="alert" aria-live="assertive">\{account\.paymentStatus\.lastFailureReason\}/);
  });
});
