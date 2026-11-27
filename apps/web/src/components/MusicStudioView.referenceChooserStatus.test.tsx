import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { Asset } from "@/lib/api";
import { ReferenceAudioPanel } from "./MusicStudioView";
import type { ReferenceAudioCandidate, ReferenceAudioState } from "@/lib/musicReferenceAudioState";

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
const emptyState: ReferenceAudioState = { candidate: null, previewObjectUrl: null, rejectedReasonKey: null };

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

function storedCandidate(asset: Asset): ReferenceAudioCandidate {
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

const localCandidate = {
  id: "local:2048:loop.mp3",
  sourceKind: "local" as const,
  displayName: "loop.mp3",
  extension: ".mp3",
  mimeType: "audio/mpeg",
  sizeBytes: 2048,
  assetId: null,
  durationSeconds: null,
};

function renderPanel(props: {
  state?: ReferenceAudioState;
  candidates?: ReferenceAudioCandidate[];
  loading?: boolean;
} = {}) {
  return renderToStaticMarkup(
    <ReferenceAudioPanel
      state={props.state ?? emptyState}
      candidates={props.candidates ?? []}
      loading={props.loading ?? false}
      t={translate}
      onFileSelected={() => undefined}
      onStoredPicked={() => undefined}
      onClear={() => undefined}
    />,
  );
}

function chooserSource() {
  const source = readFileSync(new URL("./MusicStudioView.tsx", import.meta.url), "utf8");
  const start = source.indexOf("export function ReferenceAudioPanel");
  const end = source.indexOf("function RecentMusicLibrary");
  expect(start).toBeGreaterThanOrEqual(0);
  expect(end).toBeGreaterThan(start);
  return source.slice(start, end);
}

describe("Music Studio reference chooser empty/loading status and focus", () => {
  it("reports the private library load through one persistent status region", () => {
    const loading = renderPanel({ loading: true, candidates: [] });
    const empty = renderPanel({ loading: false, candidates: [] });
    const ready = renderPanel({ loading: false, candidates: [storedCandidate(storedAsset())] });

    // The library query resolves asynchronously while the panel is already on
    // screen. One <p role="status"> element stays mounted across loading ->
    // empty/ready, so a screen reader hears the transition instead of watching
    // two different silent paragraphs swap.
    expect(loading).toContain('role="status"');
    expect(empty).toContain('role="status"');
    expect(ready).toContain('role="status"');

    expect(loading).toContain("music.reference.loading");
    expect(loading).not.toContain("music.reference.empty");
    expect(empty).toContain("music.reference.empty");
    expect(empty).not.toContain("music.reference.loading");
    expect(ready).not.toContain("music.reference.loading");
    expect(ready).not.toContain("music.reference.empty");
  });

  it("keeps the announced selection region mounted after the clip is removed", () => {
    const selected = renderPanel({
      state: { candidate: localCandidate, previewObjectUrl: "blob:preview-1", rejectedReasonKey: null },
    });
    const removed = renderPanel();

    // aria-live regions only speak for changes inside an already-mounted
    // region. The wrapper survives removal so "Remove reference" is announced
    // instead of the region vanishing along with its content.
    expect(selected).toContain('aria-live="polite"');
    expect(removed).toContain('aria-live="polite"');
    expect(removed).not.toContain("music.reference.clear");
    expect(removed).not.toContain("<audio");
  });

  it("moves focus to the surviving chooser button when the selection is removed", () => {
    const panel = chooserSource();

    // The clear button lives inside the block that unmounts on removal. It
    // hands focus to the persistent "Choose from device" button before its
    // container goes away, so the keyboard user keeps their place in the
    // panel instead of being dumped on <body>.
    expect(panel).toContain("chooseButtonRef.current?.focus()");
    expect(panel).toMatch(/onClick=\{handleClear\}/);
    expect(panel).toMatch(/<button type="button" ref=\{chooseButtonRef\} className="music-action-button"/);

    // Regression guard: the unmounting button must never own the click again.
    expect(panel).not.toMatch(/music-reference-clear[^>]*onClick=\{onClear\}/);
  });

  it("mounts the status and live regions unconditionally in the source", () => {
    const panel = chooserSource();

    // Structure pinned at source level because both transitions matter and
    // renderToStaticMarkup cannot show a swap. The status paragraph and the
    // live wrapper must sit outside their conditionals.
    expect(panel).toMatch(/<p className="music-reference-muted music-reference-status" role="status">/);
    expect(panel).toMatch(/<div className="music-reference-live" aria-live="polite">\s*\{selected \?/);
    // The saved-audio select is the only element that may replace the status
    // text, and only once the library has answered.
    expect(panel).toMatch(/\{!loading && candidates\.length > 0 \? <label/);
  });

  it("still offers the saved-audio select only from the candidates it was given", () => {
    const candidates = [
      storedCandidate(storedAsset({ id: "asset-a", name: "a-loop.mp3" })),
      storedCandidate(storedAsset({ id: "asset-b", name: "b-loop.wav", mimeType: "audio/wav" })),
    ];
    const html = renderPanel({ candidates });
    expect(html).toContain("music-reference-select");
    expect(html).toContain('<option value="asset-a">a-loop.mp3</option>');
    expect(html).toContain('<option value="asset-b">b-loop.wav</option>');
    // No provider call and no upload leaves the chooser, as before.
    expect(html).not.toContain("suno");
    expect(html).not.toContain("mubert");
  });
});
