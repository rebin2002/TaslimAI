// @vitest-environment jsdom
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Asset, Project } from "@/lib/api";

const harness = vi.hoisted(() => ({
  workspace: { id: "workspace-a" },
  t: (key: string) => key,
  api: {
    listProjects: vi.fn(),
    listAssets: vi.fn(),
    createMusicGenerationJob: vi.fn(),
    getGenerationJob: vi.fn(),
    cancelGenerationJob: vi.fn(),
    assetFileUrl: vi.fn((id: string, inline?: boolean) => `/api/assets/${id}/download${inline ? "?inline=true" : ""}`),
  },
}));

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: harness.workspace, user: { id: "synthetic-member" } }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: harness.t }),
}));
vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams() }));
vi.mock("next/link", () => ({
  default: ({ href, children, ...props }: { href: string; children?: React.ReactNode }) => <a href={href} {...props}>{children}</a>,
}));
vi.mock("@/lib/api", () => ({ api: harness.api }));
vi.mock("@/components/AccessibleProgressBar", () => ({ AccessibleProgressBar: () => <div /> }));
vi.mock("@/components/MediaJobArtifactDownload", () => ({ MediaJobArtifactDownload: () => <div /> }));
vi.mock("@/components/music/LyricsSectionEditor", () => ({ LyricsSectionEditor: () => <div /> }));
vi.mock("@/components/music/MusicLyricsPreflightPanel", () => ({ MusicLyricsPreflightPanel: () => <div /> }));
vi.mock("lucide-react", () => {
  const Icon = () => null;
  return {
    AlertCircle: Icon, AudioLines: Icon, CheckCircle2: Icon, Clock3: Icon, Download: Icon,
    FolderOpen: Icon, Headphones: Icon, Info: Icon, LoaderCircle: Icon, Music2: Icon,
    Pause: Icon, Play: Icon, RefreshCw: Icon, Sparkles: Icon, WandSparkles: Icon, XCircle: Icon,
  };
});

import { MusicStudioView } from "./MusicStudioView";

type AssetPage = { items: Asset[]; totalCount: number; page: number; pageSize: number; totalPages: number };
type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (reason: unknown) => void };

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
function project(workspaceId: string, id: string, name: string): Project {
  return {
    id, workspaceId, name, description: null, type: "General", status: "Active",
    instructions: null, contextNotes: null, archivedAt: null,
    createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
  };
}
function musicAsset(workspaceId: string, id: string, name: string): Asset {
  return {
    id, workspaceId, projectId: null, projectName: null, name, description: null,
    assetType: "music", mimeType: "audio/mpeg", status: "Active", hasFile: true, canPreview: true,
    fileSizeBytes: 2048, sourceStudio: "music", sourceJobTitle: name,
    createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z", archivedAt: null, representations: [],
  };
}
function page(items: Asset[]): AssetPage {
  return { items, totalCount: items.length, page: 1, pageSize: 100, totalPages: 1 };
}
function findProjectSelect(container: HTMLElement): HTMLSelectElement {
  const select = Array.from(container.querySelectorAll<HTMLSelectElement>(".music-optional-grid select"))
    .find((node) => Array.from(node.options).some((option) => option.textContent === "music.noProject"));
  if (!select) throw new Error("Music Studio project selector was not rendered");
  return select;
}
async function setControlValue(control: HTMLSelectElement | HTMLTextAreaElement, value: string) {
  await act(async () => {
    const prototype = control instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLTextAreaElement.prototype;
    Object.getOwnPropertyDescriptor(prototype, "value")?.set?.call(control, value);
    control.dispatchEvent(new Event(control instanceof HTMLSelectElement ? "change" : "input", { bubbles: true }));
  });
}
async function flush() {
  await act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });
}

let container: HTMLDivElement;
let root: Root;
async function mount() {
  await act(async () => {
    root = createRoot(container);
    root.render(<MusicStudioView />);
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}
async function switchWorkspace(id: string) {
  await act(async () => {
    harness.workspace = { id };
    root.render(<MusicStudioView />);
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

beforeEach(() => {
  vi.stubGlobal("IS_REACT_ACT_ENVIRONMENT", true);
  container = document.createElement("div");
  document.body.appendChild(container);
  harness.workspace = { id: "workspace-a" };
  for (const method of Object.values(harness.api)) method.mockReset();
  harness.api.assetFileUrl.mockImplementation((id: string, inline?: boolean) => `/api/assets/${id}/download${inline ? "?inline=true" : ""}`);
});
afterEach(() => {
  if (root) act(() => root.unmount());
  container.remove();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Music Studio workspace switching", () => {
  it("clears resolved workspace UI, reports new-workspace list failures, and keeps the selected workspace editable", async () => {
    const projectA = project("workspace-a", "project-a", "Workspace A project");
    const projectB = project("workspace-b", "project-b", "Workspace B project");
    harness.api.listProjects.mockImplementation((workspaceId: string, status: string) => (
      Promise.resolve(workspaceId === "workspace-a" && status === "Active" ? [projectA] : workspaceId === "workspace-b" && status === "Active" ? [projectB] : [])
    ));
    harness.api.listAssets.mockImplementation((workspaceId: string, query: { assetType?: string }) => {
      if (workspaceId === "workspace-a") return Promise.resolve(page([musicAsset("workspace-a", query.assetType ? "a-track" : "a-reference", query.assetType ? "A recent track" : "A reference clip.mp3")]));
      return Promise.reject(new Error("synthetic workspace-B list failure"));
    });

    await mount();
    expect(container.textContent).toContain("A recent track");
    expect(Array.from(findProjectSelect(container).options).map((option) => option.textContent)).toContain("Workspace A project");
    expect(container.textContent).toContain("A reference clip.mp3");
    await setControlValue(findProjectSelect(container), "project-a");
    await setControlValue(container.querySelector<HTMLTextAreaElement>('textarea[aria-label="music.descriptionLabel"]')!, "private A draft");
    await setControlValue(container.querySelector<HTMLTextAreaElement>('textarea[aria-label="music.purpose"]')!, "A purpose");

    await switchWorkspace("workspace-b");
    await flush();
    expect(container.textContent).not.toContain("A recent track");
    expect(container.textContent).not.toContain("A reference clip.mp3");
    expect(Array.from(findProjectSelect(container).options).map((option) => option.textContent)).not.toContain("Workspace A project");
    expect(findProjectSelect(container).value).toBe("");
    expect(container.querySelector<HTMLTextAreaElement>('textarea[aria-label="music.descriptionLabel"]')?.value).toBe("");
    expect(container.querySelector<HTMLTextAreaElement>('textarea[aria-label="music.purpose"]')?.value).toBe("");
    expect(container.textContent).toContain("music.libraryLoadError");
    expect(container.textContent).toContain("music.reference.empty");

    await setControlValue(findProjectSelect(container), "project-b");
    await setControlValue(container.querySelector<HTMLTextAreaElement>('textarea[aria-label="music.descriptionLabel"]')!, "new workspace brief");
    await setControlValue(container.querySelector<HTMLTextAreaElement>('textarea[aria-label="music.purpose"]')!, "new workspace purpose");
    expect(findProjectSelect(container).value).toBe("project-b");
    expect(container.querySelector<HTMLTextAreaElement>('textarea[aria-label="music.descriptionLabel"]')?.value).toBe("new workspace brief");
    expect(container.querySelector<HTMLTextAreaElement>('textarea[aria-label="music.purpose"]')?.value).toBe("new workspace purpose");
    expect(harness.api.createMusicGenerationJob).not.toHaveBeenCalled();
    expect(harness.api.getGenerationJob).not.toHaveBeenCalled();
    expect(harness.api.cancelGenerationJob).not.toHaveBeenCalled();
  });

  it("keeps B's loading and resolved state when delayed A successes and a failed A request settle out of order", async () => {
    const aProjectsActive = deferred<Project[]>();
    const aProjectsArchived = deferred<Project[]>();
    const aRecentMusic = deferred<AssetPage>();
    const aReferenceAssets = deferred<AssetPage>();
    const bProjectsActive = deferred<Project[]>();
    const bProjectsArchived = deferred<Project[]>();
    const bRecentMusic = deferred<AssetPage>();
    const bReferenceAssets = deferred<AssetPage>();
    harness.api.listProjects.mockImplementation((workspaceId: string, status: string) => {
      if (workspaceId === "workspace-a") return status === "Active" ? aProjectsActive.promise : aProjectsArchived.promise;
      return status === "Active" ? bProjectsActive.promise : bProjectsArchived.promise;
    });
    harness.api.listAssets.mockImplementation((workspaceId: string, query: { assetType?: string }) => {
      if (workspaceId === "workspace-a") return query.assetType ? aRecentMusic.promise : aReferenceAssets.promise;
      return query.assetType ? bRecentMusic.promise : bReferenceAssets.promise;
    });

    await mount();
    expect(container.textContent).toContain("music.libraryLoading");
    await switchWorkspace("workspace-b");
    expect(container.textContent).not.toContain("workspace-a");
    expect(container.textContent).toContain("music.libraryLoading");
    expect(container.textContent).toContain("music.reference.loading");

    bProjectsActive.resolve([project("workspace-b", "project-b", "Workspace B project")]);
    bProjectsArchived.resolve([]);
    bRecentMusic.resolve(page([musicAsset("workspace-b", "b-track", "B recent track")]));
    bReferenceAssets.resolve(page([musicAsset("workspace-b", "b-reference", "B reference clip.mp3")]));
    await flush();
    expect(container.textContent).toContain("B recent track");
    expect(container.textContent).toContain("B reference clip.mp3");
    await setControlValue(findProjectSelect(container), "project-b");

    // Resolve the old library and reference reads first, then fail the old project batch.
    // None may replace, clear, or relabel the currently selected B view.
    aRecentMusic.resolve(page([musicAsset("workspace-a", "late-a-track", "Late A track")]));
    aReferenceAssets.resolve(page([musicAsset("workspace-a", "late-a-reference", "Late A reference.mp3")]));
    aProjectsArchived.resolve([project("workspace-a", "late-a-project", "Late A project")]);
    aProjectsActive.reject(new Error("synthetic delayed A project failure"));
    await flush();

    expect(container.textContent).toContain("B recent track");
    expect(container.textContent).toContain("B reference clip.mp3");
    expect(container.textContent).not.toContain("Late A track");
    expect(container.textContent).not.toContain("Late A reference.mp3");
    expect(Array.from(findProjectSelect(container).options).map((option) => option.textContent)).toContain("Workspace B project");
    expect(Array.from(findProjectSelect(container).options).map((option) => option.textContent)).not.toContain("Late A project");
    expect(findProjectSelect(container).value).toBe("project-b");
    expect(harness.api.createMusicGenerationJob).not.toHaveBeenCalled();
    expect(harness.api.getGenerationJob).not.toHaveBeenCalled();
    expect(harness.api.cancelGenerationJob).not.toHaveBeenCalled();
  });
});
