import { afterEach, describe, expect, it, vi } from "vitest";

afterEach(() => vi.unstubAllGlobals());

describe("research source export API", () => {
  it("downloads the private manifest with the authenticated session", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response("citationId,sourceType\nS1,web\n", { status: 200, headers: { "Content-Type": "text/csv" } }));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");

    const blob = await api.downloadResearchSourcesExport("job-1");

    expect(await blob.text()).toContain("S1,web");
    expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining("/api/research-generation/jobs/job-1/sources/export"), { credentials: "include" });
  });

  it("normalizes an unavailable manifest into a safe typed error", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 404 }));
    vi.stubGlobal("fetch", fetchMock);
    const { ApiError, api } = await import("./api");

    const failure = api.downloadResearchSourcesExport("missing-job");

    await expect(failure).rejects.toBeInstanceOf(ApiError);
    await expect(failure).rejects.toMatchObject({ status: 404, code: "RESEARCH_SOURCE_EXPORT_UNAVAILABLE" });
  });
});
