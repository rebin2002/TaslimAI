import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import HealthRouteError from "./error";

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    t: (key: string) => `synthetic:${key}`,
  }),
}));

describe("Health route error localization", () => {
  it("uses locale copy and keeps private error details out of accessible recovery markup", () => {
    const privateMessage = "SYNTHETIC_PRIVATE_HEALTH_ERROR";
    const privateDigest = "SYNTHETIC_HEALTH_DIGEST";
    const html = renderToStaticMarkup(
      <HealthRouteError
        error={Object.assign(new Error(privateMessage), { digest: privateDigest })}
        reset={() => undefined}
      />,
    );

    expect(html).toContain('role="alert"');
    expect(html).toContain('aria-live="assertive"');
    expect(html).toContain('aria-labelledby="health-error-title"');
    expect(html).toContain('aria-describedby="health-error-description"');
    expect(html).toContain("synthetic:health.eyebrow");
    expect(html).toContain("synthetic:error.title");
    expect(html).toContain("synthetic:error.description");
    expect(html).toContain('type="button"');
    expect(html).toContain("synthetic:error.retry");
    expect(html).toContain('href="/"');
    expect(html).toContain("synthetic:placeholder.backHome");
    expect(html).not.toContain(privateMessage);
    expect(html).not.toContain(privateDigest);
  });
});
