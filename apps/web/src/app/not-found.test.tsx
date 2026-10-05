import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { LocaleProvider } from "@/components/LocaleProvider";
import NotFound from "./not-found";

describe("not-found boundary", () => {
  it("renders accessible recovery navigation without exposing internal details", () => {
    const html = renderToStaticMarkup(
      <LocaleProvider>
        <NotFound />
      </LocaleProvider>,
    );

    expect(html).toContain("Page not found");
    expect(html).toContain("We could not find that page");
    expect(html).toContain('aria-labelledby="not-found-title"');
    expect(html).toContain('aria-describedby="not-found-description"');
    expect(html).toContain('href="/"');
    expect(html).toContain('href="/assets"');
    expect(html).not.toContain("error.message");
    expect(html).not.toContain("digest");
  });
});
