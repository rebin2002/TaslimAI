import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import {
  MovieVideoMakeoverSourcePanel,
  movieVideoMakeoverSourcePanelModel,
  type MovieVideoMakeoverSourcePanelProps,
} from "./MovieVideoMakeoverSourcePanel";
import {
  describeMovieVideoMakeoverSource,
  type MovieVideoMakeoverSourceDescriptor,
  type MovieVideoMakeoverSourceEntry,
} from "@/lib/movieVideoMakeoverSourceCatalog";

const WORKSPACE = "workspace_01";
const PRINCIPAL = "principal_creator_01";

function source(overrides: Partial<MovieVideoMakeoverSourceDescriptor> = {}): MovieVideoMakeoverSourceDescriptor {
  return {
    assetId: "asset_take_01",
    name: "Talking head take 01",
    assetType: "video",
    mimeType: "video/mp4",
    clipStatus: "Succeeded",
    workspaceId: WORKSPACE,
    ownerPrincipalId: PRINCIPAL,
    visibility: "private",
    retentionPolicyVerified: true,
    hasFile: true,
    canPreview: true,
    sizeBytes: 40_000_000,
    durationSeconds: 12,
    width: 1080,
    height: 1920,
    frameRate: 30,
    audioPresent: true,
    ...overrides,
  };
}

function entry(overrides: Partial<MovieVideoMakeoverSourceDescriptor> = {}): MovieVideoMakeoverSourceEntry {
  return describeMovieVideoMakeoverSource(source(overrides), { principalId: PRINCIPAL, workspaceId: WORKSPACE });
}

/** Button labels in rendered markup; split-based because the target is ES2017. */
function buttonLabels(markup: string): string[] {
  return markup
    .split("<button")
    .slice(1)
    .map((fragment) => {
      const open = fragment.indexOf(">");
      const close = fragment.indexOf("</button>");
      return open === -1 || close === -1 ? "" : fragment.slice(open + 1, close);
    });
}

const handlers: Pick<MovieVideoMakeoverSourcePanelProps, "onAttach" | "onDetach"> = {
  onAttach: () => {
    throw new Error("static render must not invoke onAttach");
  },
  onDetach: () => {
    throw new Error("static render must not invoke onDetach");
  },
};

function render(
  entries: readonly MovieVideoMakeoverSourceEntry[],
  selectedAssetId: string | null = null,
  mode: string | null = "studio-swap",
): string {
  return renderToStaticMarkup(
    <MovieVideoMakeoverSourcePanel
      entries={entries}
      selectedAssetId={selectedAssetId}
      mode={mode}
      {...handlers}
    />,
  );
}

describe("source panel model: states a creator can act on", () => {
  it("marks one attachable clip and nothing selected", () => {
    const model = movieVideoMakeoverSourcePanelModel([entry()], null, "studio-swap");
    expect(model.rows).toHaveLength(1);
    expect(model.rows[0].attachable).toBe(true);
    expect(model.rows[0].attached).toBe(false);
    expect(model.notice).toBe("1 of 1 clip can be attached as a source.");
  });

  it("keeps a non-video candidate visible with its reason instead of hiding it", () => {
    const model = movieVideoMakeoverSourcePanelModel([
      entry(),
      entry({ assetId: "asset_still", assetType: "image", mimeType: "image/png" }),
    ], null, "studio-swap");
    expect(model.rows).toHaveLength(2);
    expect(model.notice).toBe("1 of 2 clips can be attached as a source.");
    const blocked = model.rows.find((row) => row.assetId === "asset_still");
    expect(blocked?.attachable).toBe(false);
    expect(blocked?.reasons.join(" ")).toMatch(/finished video asset/i);
  });

  it("explains an empty catalogue without inventing an upload path", () => {
    const model = movieVideoMakeoverSourcePanelModel([], null, "studio-swap");
    expect(model.rows).toEqual([]);
    expect(model.notice).toMatch(/finish a short-form movie render/i);
    expect(model.notice).toMatch(/upload a video asset/i);
  });

  it("summarises a catalogue where nothing qualifies", () => {
    const model = movieVideoMakeoverSourcePanelModel(
      [entry({ clipStatus: "Running" }), entry({ hasFile: false })],
      null,
      "relight",
    );
    expect(model.notice).toMatch(/none of these clips can be a makeover source yet/i);
  });

  it("lists the facts still unverified on the attached clip", () => {
    const attached = entry({ frameRate: null, audioPresent: null });
    const model = movieVideoMakeoverSourcePanelModel([attached], attached.assetId, "restyle");
    expect(model.rows[0].attached).toBe(true);
    expect(model.rows[0].pendingFacts).toHaveLength(2);
    expect(model.headline).toMatch(/2 source facts still need verification/i);
  });

  it("answers an outfit request as a future feature, not a source problem", () => {
    for (const mode of ["wardrobe-change", "outfit-change"]) {
      const model = movieVideoMakeoverSourcePanelModel([entry()], "asset_take_01", mode);
      expect(model.headline).toMatch(/outfit change is a future feature/i);
      expect(model.details.join(" ")).not.toMatch(/attach/i);
    }
  });
});

describe("source panel markup: honest and inert", () => {
  it("offers attach only for attachable clips and detach only for the attached one", () => {
    const blocked = entry({ assetId: "asset_running", clipStatus: "Running" });
    const other = entry({ assetId: "asset_take_02", name: "Talking head take 02" });

    const markup = render([other, entry(), blocked], "asset_take_01");
    // One attach control (the unattached, attachable clip) and one detach control
    // (the attached clip); the blocked clip gets neither.
    expect([...markup.matchAll(/Attach as source/g)]).toHaveLength(1);
    expect([...markup.matchAll(/Detach/g)]).toHaveLength(1);
    expect(markup).toContain('data-attached="true"');
    expect(markup).toContain('data-attachable="false"');
    expect(markup).toMatch(/has not finished rendering/);
  });

  it("never renders an execution control", () => {
    const markup = render([entry()], "asset_take_01");
    // Assert on control labels, not prose: the panel is allowed to *say* that
    // estimate and upload are not wired while rendering no such control.
    const labels = buttonLabels(markup);
    expect(labels.length).toBeGreaterThan(0);
    for (const forbidden of ["Estimate", "Start makeover", "Download", "Upload", "Retry"]) {
      expect(labels.some((label) => label.toLowerCase().includes(forbidden.toLowerCase()))).toBe(false);
    }
    // The one attachable clip is already attached, so only Detach remains.
    expect(labels).toEqual(["Detach"]);
  });

  it("states that picking a source is a draft edit and that upload is not wired", () => {
    const markup = render([entry()], null);
    expect(markup).toMatch(/Picking a source only edits the draft/);
    expect(markup).toMatch(/upload from this panel is not wired/i);
    expect(markup).toMatch(/starts no job and reserves no credit/i);
  });

  it("shows the blocker text for a clip that cannot be attached", () => {
    const markup = render([entry({ assetId: "asset_img", assetType: "image" })], null);
    expect(markup).toMatch(/finished video asset/i);
    expect(markup).toMatch(/This clip has not finished rendering|Makeover takes a finished video asset/);
  });

  it("says plainly when there is nothing to pick from", () => {
    const markup = render([], null);
    expect(markup).toMatch(/Nothing to show yet/);
    expect(markup).not.toContain("Attach as source");
  });

  it("keeps the unattached headline honest about missing facts", () => {
    const markup = render([entry()], "asset_take_01");
    expect(markup).toMatch(/Source facts complete/);
    expect(markup).toMatch(/non-billable/i);
  });
});
