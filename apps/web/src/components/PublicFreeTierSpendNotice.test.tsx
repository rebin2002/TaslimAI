import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { PublicFreeTierSpendNotice } from "./PublicFreeTierSpendNotice";

describe("PublicFreeTierSpendNotice", () => {
  it("announces a quota limit and displays a valid UTC retry time", () => {
    const html = renderToStaticMarkup(
      <PublicFreeTierSpendNotice
        state="quota"
        retryAfterUtc="2026-11-02T00:00:00Z"
      />,
    );

    expect(html).toContain('role="status"');
    expect(html).toContain('aria-live="polite"');
    expect(html).toContain('aria-atomic="true"');
    expect(html).toContain('data-state="quota"');
    expect(html).toContain("Free AI limit reached");
    expect(html).toContain("Your free-plan request limit has been reached.");
    expect(html).toContain("2026-11-02 00:00 UTC");
    expect(html).toContain("Free-tier provider budgets are separate from customer credit balances.");
    expect(html).not.toContain("credits were deducted");
  });

  it("normalizes offset retry timestamps to UTC", () => {
    const html = renderToStaticMarkup(
      <PublicFreeTierSpendNotice
        state="quota"
        retryAfterUtc="2026-11-02T00:00:00-05:00"
      />,
    );

    expect(html).toContain("2026-11-02 05:00 UTC");
  });

  it("uses safe fallback copy for missing or malformed quota reset time", () => {
    for (const retryAfterUtc of [undefined, "not-a-date", "2026-11-02T00:00:00"]) {
      const html = renderToStaticMarkup(
        <PublicFreeTierSpendNotice state="quota" retryAfterUtc={retryAfterUtc} />,
      );

      expect(html).toContain("Please check again later.");
      expect(html).not.toContain("<time");
    }
  });

  it("does not claim a quota reset for pricing or usage safety unavailability", () => {
    const html = renderToStaticMarkup(
      <PublicFreeTierSpendNotice
        state="unavailable"
        retryAfterUtc="2026-11-02T00:00:00Z"
      />,
    );

    expect(html).toContain('data-state="unavailable"');
    expect(html).toContain("Free AI temporarily unavailable");
    expect(html).toContain("The server could not safely verify provider pricing or current usage");
    expect(html).toContain("Please try again later.");
    expect(html).not.toContain("<time");
    expect(html).not.toContain("credits were deducted");
  });
});
