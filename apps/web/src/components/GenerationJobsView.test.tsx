import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { GenerationJob } from "../lib/api";
import { GenerationJobDetail, GenerationJobList, GenerationJobsView, clampGenerationProgress } from "./GenerationJobsView";

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: { id: "workspace-1" } }),
}));

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({
    t: (key: string, variables?: Record<string, string>) => Object.entries(variables ?? {}).reduce((value, [name, replacement]) => value.replace(`{${name}}`, replacement), key),
  }),
}));

vi.mock("@/lib/api", () => ({
  api: {
    listGenerationJobs: vi.fn(),
    getGenerationJob: vi.fn(),
    createGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
  },
}));

const job: GenerationJob = {
  id: "job-1",
  workspaceId: "workspace-1",
  projectId: null,
  jobType: "system.test",
  status: "Running",
  title: "Accessibility check",
  progressPercent: 140,
  resultJson: null,
  errorCode: null,
  errorMessage: "The test job needs attention.",
  cancellationRequested: false,
  createdAt: "2026-01-01T00:00:00Z",
  queuedAt: null,
  startedAt: null,
  completedAt: null,
  failedAt: null,
  cancelledAt: null,
  outputs: [],
};

const t = (key: string, variables?: Record<string, string>) => Object.entries(variables ?? {}).reduce((value, [name, replacement]) => value.replace(`{${name}}`, replacement), key);

describe("Generation Jobs accessibility contract", () => {
  it("exposes a bounded progressbar and a focused live announcement for job updates", () => {
    const html = renderToStaticMarkup(<GenerationJobDetail job={job} t={t} canCancel working={false} onCancel={vi.fn()} />);

    expect(clampGenerationProgress(job.progressPercent)).toBe(100);
    expect(html).toContain('role="progressbar"');
    expect(html).toContain('aria-valuemin="0"');
    expect(html).toContain('aria-valuemax="100"');
    expect(html).toContain('aria-valuenow="100"');
    expect(html).toContain('aria-valuetext="100%"');
    expect(html).toContain('role="status"');
    expect(html).toContain('jobs.progressAnnouncement');
    expect(html).toContain('role="alert"');
    expect(html).toContain('jobs.cancel');
  });

  it("makes the selected recent job state available to keyboard and assistive-technology users", () => {
    const html = renderToStaticMarkup(<GenerationJobList jobs={[job, { ...job, id: "job-2", status: "Succeeded", progressPercent: 100, errorMessage: null }]} activeJobId="job-1" t={t} onSelect={vi.fn()} />);

    expect(html).toContain('aria-pressed="true"');
    expect(html).toContain('aria-pressed="false"');
    expect(html).toContain('aria-label="jobs.progressAnnouncement"');
    expect(html).toContain('>100%</small>');
  });

  it("announces the initial recent-job loading state", () => {
    const html = renderToStaticMarkup(<GenerationJobsView />);

    expect(html).toContain('role="status"');
    expect(html).toContain("jobs.loading");
    expect(html).toContain('type="button"');
  });
});
