import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { LocaleProvider } from "@/components/LocaleProvider";
import AppRouteError from "../error";
import DocumentStudioError from "./document/error";
import PresentationStudioError from "./presentation/error";
import SocialError from "./social/error";

describe("studio route error boundaries", () => {
  it.each([
    ["generic app", () => <AppRouteError error={new Error("redacted")} reset={vi.fn()} />],
    ["Document", () => <DocumentStudioError error={new Error("redacted")} reset={vi.fn()} />],
    ["Presentation", () => <PresentationStudioError error={new Error("redacted")} reset={vi.fn()} />],
    ["Social", () => <SocialError reset={vi.fn()} />],
  ])("keep the %s recovery panel inside the shared landmark and announce it assertively", (_name, render) => {
    const html = renderToStaticMarkup(<LocaleProvider>{render()}</LocaleProvider>);
    expect(html.match(/<main\b/g) ?? []).toHaveLength(0);
    expect(html).toContain('role="alert"');
    expect(html).toContain('aria-live="assertive"');
  });
});
