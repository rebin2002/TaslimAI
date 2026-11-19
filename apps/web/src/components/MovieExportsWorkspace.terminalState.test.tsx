import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { MovieFinalAssembly } from "@/lib/api";
import { ExportCard } from "./MovieExportsWorkspace";

// Synthetic fixtures only: no account, project, asset or provider data. The API
// marks an assembly `Cancelled` when its generation job is cancelled or its lease
// expires, and it deliberately leaves `progressPercent` at the last written value
// with no `completedAt`. These cases pin what one queue card says in that state.
function assembly(
  overrides: Partial<MovieFinalAssembly> = {},
): MovieFinalAssembly {
  return {
    id: "assembly-synthetic-1",
    movieProjectId: "movie-project-synthetic-1",
    generationJobId: "job-synthetic-1",
    outputAssetId: null,
    status: "Cancelled",
    resolutionProfile: "hd-1080p",
    outputWidth: 1920,
    outputHeight: 1080,
    timelineItemCount: 12,
    sourceTakeIds: ["take-synthetic-1", "take-synthetic-2"],
    audioMixInputCount: 3,
    captionsMode: "None",
    qcStatus: "NotRun",
    qcResultJson: null,
    provenanceJson: null,
    progressPercent: 45,
    attemptCount: 1,
    canResume: true,
    createdAt: "2026-11-16T09:00:00.000Z",
    updatedAt: "2026-11-16T09:04:00.000Z",
    completedAt: null,
    job: null,
    ...overrides,
  };
}

function card(overrides: Partial<MovieFinalAssembly> = {}) {
  return renderToStaticMarkup(
    <ExportCard assembly={assembly(overrides)} locale="en" />,
  );
}

describe("Movie export queue card states", () => {
  it("stops claiming ongoing production for a cancelled master", () => {
    const html = card();
    // The record is terminal: the queue must not render it in the same in-flight
    // family as a Queued/Assembling master, and it must not promise a download
    // that can never arrive (polling has already stopped for this status).
    expect(html).not.toContain("movie-exports-card is-pending");
    expect(html).toContain("movie-exports-card is-failed");
    expect(html).not.toContain("still being produced");
    expect(html).toContain("was cancelled");
    expect(html).toContain("Cancelled");
    // A terminal card states the frozen value it holds instead of implying motion.
    expect(html).toContain('aria-valuenow="45"');
  });

  it("keeps an assembling master in the producing state", () => {
    const html = card({ status: "Assembling", progressPercent: 45 });
    expect(html).toContain("movie-exports-card is-pending");
    expect(html).toContain("still being produced");
    expect(html).not.toContain("was cancelled");
    expect(html).toContain('aria-valuenow="45"');
  });

  it("keeps a failed attempt on its own failure copy", () => {
    const html = card({ status: "Failed" });
    expect(html).toContain("movie-exports-card is-failed");
    expect(html).toContain("did not publish an output");
    expect(html).not.toContain("was cancelled");
    expect(html).not.toContain("still being produced");
  });

  it("offers the download only once an output asset is published", () => {
    const html = card({ status: "Ready", outputAssetId: "asset-synthetic-9" });
    expect(html).toContain("movie-exports-card is-ready");
    expect(html).toContain("Download master");
    expect(html).toContain(
      "/api/movie-studio/final-assemblies/assembly-synthetic-1/download",
    );
    expect(html).not.toContain("was cancelled");
    expect(html).not.toContain("still being produced");
  });
});
