import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const workspaceSource = readFileSync(new URL("./FullMovieWorkspaceView.tsx", import.meta.url), "utf8");
const apiSource = readFileSync(new URL("../lib/api.ts", import.meta.url), "utf8");

describe("Movie scene generation quote boundary", () => {
  it("previews only the explicit provider-verified short-form profile before queuing", () => {
    expect(workspaceSource).toContain('sourceResolution: "720p"');
    expect(workspaceSource).toContain('targetResolution: "720p"');
    expect(workspaceSource).toContain('qualityTier: "Standard"');
    expect(workspaceSource).toContain('processingPath: "native"');
    expect(workspaceSource).toContain("api.previewMovieSceneGeneration(projectId, sceneId, verifiedMovieClipProfile)");
    expect(workspaceSource).toContain("The active provider verified this execution profile");
    expect(workspaceSource).toContain("Estimated provider cost (maximum)");
  });

  it("requires explicit confirmation and reuses the quote's adapter identity and idempotency key", () => {
    expect(workspaceSource).toContain('confirmationAccepted: true');
    expect(workspaceSource).toContain("Confirm estimate and queue");
    expect(workspaceSource).toContain("generationReview.preview.providerKey");
    expect(workspaceSource).toContain("generationReview.preview.providerModelKey");
    expect(workspaceSource).toContain("generationReview.preview.costGuardrails?.estimatedProviderCostUsd");
    expect(workspaceSource).toContain("expectedProviderModelKey");
    expect(apiSource).toContain("expectedProviderModelKey?: string | null");
    expect(apiSource).toContain("expectedProviderCostUsd?: number | null");
    expect(apiSource).toContain("previewMovieSceneGeneration");
    expect(apiSource).toContain("generation-preview");
  });
});
