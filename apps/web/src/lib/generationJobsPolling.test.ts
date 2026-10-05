import { describe, expect, it } from "vitest";
import type { GenerationJob } from "./api";
import { generationJobPollIntervalMs, nextGenerationJobPollDelay } from "./generationJobsPolling";

const job = (status: GenerationJob["status"]) => ({ status } as GenerationJob);

describe("generation-job polling", () => {
  it("waits two seconds between active-job detail requests", () => {
    expect(generationJobPollIntervalMs).toBe(2_000);
    expect(nextGenerationJobPollDelay(job("Running"))).toBe(2_000);
    expect(nextGenerationJobPollDelay(job("Queued"))).toBe(2_000);
  });

  it("stops scheduling requests after a job reaches a terminal state", () => {
    expect(nextGenerationJobPollDelay(null)).toBeNull();
    expect(nextGenerationJobPollDelay(job("Succeeded"))).toBeNull();
    expect(nextGenerationJobPollDelay(job("Failed"))).toBeNull();
    expect(nextGenerationJobPollDelay(job("Cancelled"))).toBeNull();
  });
});
