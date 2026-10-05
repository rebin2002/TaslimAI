import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { DevelopmentDebugGuide } from "./DevelopmentDebugGuide";
import { LocaleProvider } from "./LocaleProvider";
import { locales, translate } from "@/lib/i18n";

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: { id: "workspace-test", name: "Test workspace" } }),
}));

describe("DevelopmentDebugGuide", () => {
  it("renders an accessible, provider-neutral checklist and bounded notes field", () => {
    const html = renderToStaticMarkup(<LocaleProvider><DevelopmentDebugGuide /></LocaleProvider>);
    expect(html).toContain("Debug without guessing.");
    expect(html).toContain("Reproduce the smallest case");
    expect(html).toContain('type="checkbox"');
    expect(html).toContain('aria-labelledby="development-debug-title"');
    expect(html).toContain('role="progressbar"');
    expect(html).toContain('dir="auto"');
    expect(html).toContain('maxLength="4000"');
    expect(html).toContain("does not call a provider or send external requests");
    expect(html).not.toContain("/api/");
  });

  it("provides every guide message in every supported locale", () => {
    const keys = [
      "debugGuide.title",
      "debugGuide.subtitle",
      "debugGuide.localOnly",
      "debugGuide.progress",
      "debugGuide.notesTitle",
      "debugGuide.step.reproduce.title",
      "debugGuide.step.record.description",
    ];
    for (const locale of locales) {
      for (const key of keys) expect(translate(locale, key), `${locale}:${key}`).not.toBe(key);
    }
  });

  it("keeps the explicit debug route behind both authentication gates", () => {
    const route = readFileSync(new URL("../app/development/debug/page.tsx", import.meta.url), "utf8");
    expect(route).toContain('import { ProtectedPage } from "@/components/ProtectedPage";');
    expect(route).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(route).toContain('await requireAuthenticatedPage("/development/debug");');
    expect(route).toContain('export const dynamic = "force-dynamic";');
    expect(route).toContain("return <ProtectedPage>");
  });
});
