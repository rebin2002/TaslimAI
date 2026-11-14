import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Asset } from "@/lib/api";
import { API_URL } from "@/lib/apiBase";
import { ReferenceAudioPanel } from "./MusicStudioView";

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: { id: "workspace-1" } }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: (key: string) => key }),
}));
vi.mock("@/lib/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api")>();
  return {
    ...actual,
    api: {
      ...actual.api,
      listProjects: vi.fn(),
      listAssets: vi.fn(),
      getGenerationJob: vi.fn(),
      createMusicGenerationJob: vi.fn(),
      cancelGenerationJob: vi.fn(),
    },
  };
});

const translate = (key: string) => key;

function storedAsset(overrides: Partial<Asset> = {}): Asset {
  return {
    id: "asset-1",
    workspaceId: "workspace-1",
    projectId: null,
    projectName: null,
    name: "demo-loop.mp3",
    description: null,
    assetType: "music",
    mimeType: "audio/mpeg",
    status: "Active",
    hasFile: true,
    canPreview: true,
    fileSizeBytes: 4_200_000,
    sourceStudio: "music",
    sourceJobTitle: null,
    createdAt: "2026-11-01T10:00:00Z",
    updatedAt: "2026-11-01T10:00:00Z",
    archivedAt: null,
    representations: [],
    ...overrides,
  };
}

function storedCandidate(asset: Asset) {
  return {
    id: `asset:${asset.id}`,
    sourceKind: "stored_asset" as const,
    displayName: asset.name,
    extension: ".mp3",
    mimeType: "audio/mpeg",
    sizeBytes: asset.fileSizeBytes,
    assetId: asset.id,
    durationSeconds: null,
  };
}

const emptyState = { candidate: null, previewObjectUrl: null, rejectedReasonKey: null };

describe("Music Studio reference audio panel", () => {
  it("renders a labelled, provider-free picker with an accessible file input", () => {
    const html = renderToStaticMarkup(
      <ReferenceAudioPanel state={emptyState} candidates={[]} loading={false} t={translate}
        onFileSelected={() => undefined} onStoredPicked={() => undefined} onClear={() => undefined} />,
    );
    expect(html).toContain('class="music-reference-panel"');
    expect(html).toContain('aria-labelledby="music-reference-title"');
    expect(html).toContain('id="music-reference-title"');
    expect(html).toContain("music.reference.title");
    expect(html).toContain("music.reference.description");
    expect(html).toContain('type="file"');
    expect(html).toContain('accept=".mp3,.wav,.m4a,.ogg"');
    expect(html).toContain('aria-label="music.reference.uploadLabel"');
    expect(html).toContain("music.reference.empty");
    expect(html).not.toContain("<audio");
    expect(html).not.toContain("blob:");
  });

  it("never contacts a provider: the stored preview is the private inline asset URL only", () => {
    const candidate = storedCandidate(storedAsset());
    const html = renderToStaticMarkup(
      <ReferenceAudioPanel
        state={{ candidate, previewObjectUrl: null, rejectedReasonKey: null }}
        candidates={[candidate]} loading={false} t={translate}
        onFileSelected={() => undefined} onStoredPicked={() => undefined} onClear={() => undefined} />,
    );
    expect(html).toContain(`src="${API_URL}/api/assets/asset-1/download?inline=true"`);
    expect(html.match(/src="[^"]+"/g)).toHaveLength(1);
    expect(html).toContain('controls=""');
    expect(html).toContain('preload="metadata"');
    expect(html).toContain('aria-label="music.reference.previewLabel: demo-loop.mp3"');
    expect(html).toContain("music.reference.note");
    expect(html).toContain("music.reference.clear");
    expect(html).not.toContain("suno");
    expect(html).not.toContain("Suno");
    expect(html).not.toContain("mubert");
    expect(html).not.toContain("stable-audio");
    expect(html).not.toContain("resultJson");
  });

  it("previews a locally picked clip through its object URL without uploading", () => {
    const local = {
      id: "local:2048:loop.mp3",
      sourceKind: "local" as const,
      displayName: "loop.mp3",
      extension: ".mp3",
      mimeType: "audio/mpeg",
      sizeBytes: 2048,
      assetId: null,
      durationSeconds: null,
    };
    const html = renderToStaticMarkup(
      <ReferenceAudioPanel
        state={{ candidate: local, previewObjectUrl: "blob:preview-1", rejectedReasonKey: null }}
        candidates={[]} loading={false} t={translate}
        onFileSelected={() => undefined} onStoredPicked={() => undefined} onClear={() => undefined} />,
    );
    expect(html).toContain('src="blob:preview-1"');
    expect(html).toContain("2 KiB");
    expect(html).toContain('aria-live="polite"');
    expect(html).not.toContain("/api/assets/");
  });

  it("surfaces rejection reasons and the loading state instead of stale library rows", () => {
    const rejected = renderToStaticMarkup(
      <ReferenceAudioPanel
        state={{ ...emptyState, rejectedReasonKey: "music.reference.reason.mimeMismatch" }}
        candidates={[]} loading={false} t={translate}
        onFileSelected={() => undefined} onStoredPicked={() => undefined} onClear={() => undefined} />,
    );
    expect(rejected).toContain('role="alert"');
    expect(rejected).toContain("music.reference.reason.mimeMismatch");
    const loading = renderToStaticMarkup(
      <ReferenceAudioPanel
        state={emptyState} candidates={[]} loading t={translate}
        onFileSelected={() => undefined} onStoredPicked={() => undefined} onClear={() => undefined} />,
    );
    expect(loading).toContain("music.reference.loading");
    expect(loading).not.toContain("music.reference.empty");
  });

  it("lists only the unclaimed candidates it was given", () => {
    const candidates = [
      storedCandidate(storedAsset({ id: "asset-a", name: "a-loop.mp3", createdAt: "2026-11-02T10:00:00Z" })),
      storedCandidate(storedAsset({ id: "asset-b", name: "b-loop.wav", createdAt: "2026-11-01T10:00:00Z", mimeType: "audio/wav" })),
    ];
    const html = renderToStaticMarkup(
      <ReferenceAudioPanel
        state={emptyState} candidates={candidates} loading={false} t={translate}
        onFileSelected={() => undefined} onStoredPicked={() => undefined} onClear={() => undefined} />,
    );
    expect(html).toContain("music-reference-select");
    expect(html).toContain("<option value=\"asset-a\">a-loop.mp3</option>");
    expect(html).toContain("<option value=\"asset-b\">b-loop.wav</option>");
    expect(html).toContain("music.reference.savedPlaceholder");
  });

  it("exposes the selection callbacks on interactive elements only", () => {
    const onFileSelected = vi.fn();
    const onClear = vi.fn();
    const html = renderToStaticMarkup(
      <ReferenceAudioPanel
        state={emptyState} candidates={[]} loading={false} t={translate}
        onFileSelected={onFileSelected} onStoredPicked={() => undefined} onClear={onClear} />,
    );
    expect(html).toContain("music.reference.chooseFile");
    // Server rendering has no interaction; the handlers must exist and be
    // attached to the file input only through React props (not inline hrefs).
    expect(onFileSelected).not.toHaveBeenCalled();
    expect(onClear).not.toHaveBeenCalled();
    expect(html).not.toContain("href=\"javascript:");
  });
});
