import type { GenerationJob } from "./api";
import { isTerminalJob } from "./generationJobsState";

// A generation-job detail request performs authenticated database work. Two
// seconds keeps progress responsive without repeatedly querying the same row.
export const generationJobPollIntervalMs = 2_000;

export function nextGenerationJobPollDelay(job: GenerationJob | null | undefined) {
  return job && !isTerminalJob(job) ? generationJobPollIntervalMs : null;
}
