import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("next/navigation", () => ({
  notFound: vi.fn(() => { throw new Error("NOT_FOUND"); }),
}));
vi.mock("@/lib/serverAuth", () => ({
  requireAuthenticatedPage: vi.fn(async () => undefined),
  internalReturnPath: vi.fn((pathname: string) => pathname),
}));
vi.mock("@/components/ProtectedPage", () => ({ ProtectedPage: () => null }));
vi.mock("@/components/academic/AcademicResearchStudio", () => ({ AcademicResearchStudio: () => null }));
vi.mock("@/components/academic/templates/AcademicReportExportPanel", () => ({ AcademicReportExportPanel: () => null }));

const { notFound } = await import("next/navigation");
const { internalReturnPath, requireAuthenticatedPage } = await import("@/lib/serverAuth");
const { academicExportRoute, academicRoute } = await import("@/components/academic/academicRoutes");
const { default: studioPage } = await import("./page");
const { default: exportPage } = await import("./export/page");

beforeEach(() => {
  vi.stubEnv("NEXT_PUBLIC_ACADEMIC_RESEARCH_ENABLED", "false");
  vi.mocked(notFound).mockClear();
  vi.mocked(requireAuthenticatedPage).mockClear().mockResolvedValue(undefined);
  vi.mocked(internalReturnPath).mockClear().mockImplementation((pathname) => pathname);
});

afterEach(() => vi.unstubAllEnvs());

describe("Academic Research route visibility boundary", () => {
  it("returns notFound on both signed-in routes when visibility is disabled", async () => {
    await expect(studioPage()).rejects.toThrow("NOT_FOUND");
    expect(requireAuthenticatedPage).toHaveBeenCalledWith(academicRoute);
    expect(notFound).toHaveBeenCalledTimes(1);
    expect(vi.mocked(requireAuthenticatedPage).mock.invocationCallOrder[0])
      .toBeLessThan(vi.mocked(notFound).mock.invocationCallOrder[0]);

    vi.mocked(requireAuthenticatedPage).mockClear();
    vi.mocked(notFound).mockClear();
    const searchParams = { job: "1f2e3d4c-b5a6-4789-abcd-0123456789ab" };
    await expect(exportPage({ searchParams: Promise.resolve(searchParams) })).rejects.toThrow("NOT_FOUND");
    expect(internalReturnPath).toHaveBeenCalledWith(academicExportRoute, searchParams);
    expect(requireAuthenticatedPage).toHaveBeenCalledWith(academicExportRoute);
    expect(notFound).toHaveBeenCalledTimes(1);
    expect(vi.mocked(requireAuthenticatedPage).mock.invocationCallOrder[0])
      .toBeLessThan(vi.mocked(notFound).mock.invocationCallOrder[0]);
  });

  it("returns the protected route elements only for the explicit true opt-in", async () => {
    vi.stubEnv("NEXT_PUBLIC_ACADEMIC_RESEARCH_ENABLED", "true");

    await expect(studioPage()).resolves.toBeDefined();
    await expect(exportPage({ searchParams: Promise.resolve({}) })).resolves.toBeDefined();
    expect(notFound).not.toHaveBeenCalled();
  });
});
