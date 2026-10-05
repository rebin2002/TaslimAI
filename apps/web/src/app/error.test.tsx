import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { LocaleProvider } from "@/components/LocaleProvider";
import AppRouteError from "./error";
import GlobalError from "./global-error";

describe("application error recovery boundaries", () => {
  it("renders localized generic recovery copy without exposing the error message", () => {
    const html = renderToStaticMarkup(
      <LocaleProvider>
        <AppRouteError error={Object.assign(new Error("private provider response"), { digest: "digest-1" })} reset={vi.fn()} />
      </LocaleProvider>,
    );

    expect(html).toContain("Something went wrong");
    expect(html).toContain("Try again");
    expect(html).toContain("Open Assets");
    expect(html).toContain('role="alert"');
    expect(html).not.toContain("private provider response");
    expect(html).not.toContain("digest-1");
  });

  it("keeps the provider-independent root fallback redacted", () => {
    const html = renderToStaticMarkup(
      <GlobalError error={Object.assign(new Error("private database detail"), { digest: "digest-2" })} reset={vi.fn()} />,
    );

    expect(html).toContain("Something went wrong");
    expect(html).toContain("Try again");
    expect(html).toContain('href="/assets"');
    expect(html).toContain('role="alert"');
    expect(html).not.toContain("private database detail");
    expect(html).not.toContain("digest-2");
  });

  it("emits canonical English root metadata during server recovery", () => {
    const html = renderToStaticMarkup(
      <GlobalError error={new Error("private root detail")} reset={vi.fn()} />,
    );

    expect(html).toContain('<html lang="en" dir="ltr">');
    expect(html).toContain('<body dir="ltr"');
  });
});
