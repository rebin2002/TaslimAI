import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import ResearchStudioError from "./error";
import { LocaleProvider } from "@/components/LocaleProvider";

describe("Research route error accessibility", () => {
  it("keeps the route recovery inside the shared page landmark", () => {
    const html = renderToStaticMarkup(
      <LocaleProvider>
        <ResearchStudioError error={new Error("hidden") as Error & { digest?: string }} reset={() => undefined} />
      </LocaleProvider>,
    );

    expect(html.match(/<main\b/g) ?? []).toHaveLength(0);
    expect(html).toContain('role="alert"');
    expect(html).toContain('aria-live="assertive"');
  });
});
