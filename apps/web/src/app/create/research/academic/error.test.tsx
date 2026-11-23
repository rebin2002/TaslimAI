/**
 * Issue #1259 (Academic Research Studio) — recovery boundary tests.
 *
 * The academic workspace holds a student's unpublished plan, so a failure here
 * must be announced to assistive technology, must return focus to the recovery
 * panel, and must never echo the underlying error, which can contain private
 * brief text. Mirrors the convention in `src/app/create/research/error.test.tsx`.
 */
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { LocaleProvider } from "@/components/LocaleProvider";
import AcademicResearchStudioError from "./error";

describe("Academic route error boundary", () => {
  const error = Object.assign(new Error("PRIVATE_MARKER_question text"), { digest: "PRIVATE_MARKER_digest" });
  const html = renderToStaticMarkup(
    <LocaleProvider>
      <AcademicResearchStudioError error={error} reset={() => undefined} />
    </LocaleProvider>,
  );

  it("announces the failure assertively inside the shared landmark", () => {
    expect(html.match(/<main\b/g) ?? []).toHaveLength(0);
    expect(html).toContain('role="alert"');
    expect(html).toContain('aria-live="assertive"');
    expect(html).toContain('tabindex="-1"');
    expect(html).toContain('aria-labelledby="academic-error-title"');
    expect(html).toContain('aria-describedby="academic-error-description"');
  });

  it("offers retry first and keeps the student inside the product", () => {
    expect(html).toContain("Try again");
    expect(html).toContain('href="/create/research/academic"');
    expect(html).toContain('href="/assets"');
  });

  it("never renders the error message or digest, which can carry private brief text", () => {
    expect(html).not.toContain("PRIVATE_MARKER");
  });
});
