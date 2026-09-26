import { beforeEach, describe, expect, it, vi } from "vitest";

function workspaceResponse() {
  return {
    module: "cast",
    project: {
      id: "movie-1",
      workspaceId: "workspace-1",
      projectId: null,
      mode: "Full",
      status: "Draft",
      title: "Compact workspace",
      description: "A compact test project.",
      durationSeconds: 60,
      aspectRatio: "16:9",
      style: "cinematic",
      language: "en",
      additionalInstructions: null,
      createdAt: "2026-09-26T00:00:00Z",
      updatedAt: "2026-09-26T00:00:00Z",
      guide: {
        id: "guide-1",
        visualLanguage: "Natural",
        cameraLanguage: "Quiet",
        colorAndLighting: "Warm",
        soundAndNarration: "Sparse",
        continuityRules: "Keep the scar visible.",
        updatedAt: "2026-09-26T00:00:00Z",
      },
      scenes: [],
      characters: [],
      locations: [],
      clips: [],
      assemblies: [],
      world: null,
    },
  };
}

describe("api.getMovieWorkspace", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

  it("deduplicates concurrent room requests without caching completed responses", async () => {
    let resolveResponse!: (value: Response) => void;
    const fetchMock = vi.fn().mockReturnValue(new Promise<Response>((resolve) => { resolveResponse = resolve; }));
    vi.stubGlobal("fetch", fetchMock);
    const { api } = await import("./api");

    const first = api.getMovieWorkspace("movie-1", "cast");
    const second = api.getMovieWorkspace("movie-1", "cast");
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toContain("/api/movie-studio/projects/movie-1/workspace?module=cast");

    resolveResponse(new Response(JSON.stringify(workspaceResponse()), { status: 200, headers: { "Content-Type": "application/json" } }));
    const [firstResult, secondResult] = await Promise.all([first, second]);
    expect(firstResult).toEqual(secondResult);

    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(workspaceResponse()), { status: 200, headers: { "Content-Type": "application/json" } }));
    await api.getMovieWorkspace("movie-1", "cast");
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
