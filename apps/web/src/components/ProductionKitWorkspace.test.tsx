import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { kitRecordForTest } from "./ProductionKitWorkspace";
import type { MovieCast, MovieWorldWorkspace } from "@/lib/api";

const cast = { project: {} as MovieCast["project"], characters: [{ id: "character-1", name: "Mara", role: "Lead", description: "A patient cartographer.", appearance: null, referenceAssetId: "asset-face", referenceAssetIds: ["asset-face"], referenceAssetCount: 1, stateCount: 1, latestState: null, relationshipCount: 0, relationshipTypes: [], lockedFactCount: 1, lockedFieldKeys: ["name"], updatedAt: "2026-09-30T00:00:00Z" }] } as MovieCast;
const world = { movieProjectId: "movie-1", workspaceId: "workspace-1", projectId: null, status: "Draft", title: "North Star", description: "A film.", durationSeconds: 60, aspectRatio: "16:9", style: "cinematic", language: "en", world: { locations: [{ id: "location-1", name: "Harbor", description: "A cold harbor.", visualContinuityNotes: null, referenceAssetId: null }], sets: [], props: [{ id: "prop-1", name: "Compass", description: "A brass compass.", category: "Hero prop", continuityNotes: null, referenceAssetId: "asset-compass" }], references: [], usages: [], facts: [], locks: [{ id: "lock-1", entityType: "prop", entityId: "prop-1", fieldName: "name", lockedValue: "Compass", strength: "hard", reason: null, createdAt: "2026-09-30T00:00:00Z", releasedAt: null }] }, usageDetails: [], assets: [] } as MovieWorldWorkspace;

describe("Production Kit workspace", () => {
  it("derives readiness from references and persisted locks", () => {
    expect(kitRecordForTest("characters", cast, world)[0].state).toBe("ready");
    expect(kitRecordForTest("locations", cast, world)[0].state).toBe("needs-reference");
    expect(kitRecordForTest("props", cast, world)[0].state).toBe("ready");
  });

  it("keeps the three-room hierarchy and safe user-facing boundaries explicit", () => {
    const source = readFileSync(new URL("./ProductionKitWorkspace.tsx", import.meta.url), "utf8");
    expect(source).toContain('id: "characters"');
    expect(source).toContain('id: "locations"');
    expect(source).toContain('id: "props"');
    expect(source).toContain("Needs reference");
    expect(source).toContain("movieBody.kit.approveIdentity");
    expect(source).toContain("movieBody.world.openLibrary");
    expect(source).not.toContain("providerName");
    expect(source).not.toContain("modelName");
    expect(source).not.toContain("promptName");
  });
});
