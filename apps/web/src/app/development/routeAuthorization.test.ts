import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const routes = [
  ["code/page.tsx", "/development/code", "feature.code"],
  ["projects/page.tsx", "/development/projects", "feature.projects"],
] as const;

describe("development placeholder route authorization", () => {
  it.each(routes)("protects %s with server and client authentication", (relativePath, route, labelKey) => {
    const source = readFileSync(new URL(`./${relativePath}`, import.meta.url), "utf8");
    expect(source).toContain('import { ProtectedPage } from "@/components/ProtectedPage";');
    expect(source).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain(`await requireAuthenticatedPage("${route}");`);
    expect(source).toContain(`<ProtectedPage><PlaceholderPage nameKey="${labelKey}" /></ProtectedPage>`);
  });
});
