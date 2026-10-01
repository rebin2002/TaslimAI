import { describe, expect, it } from "vitest";
import { directorActionTypeLabel, directorHistoryLabel, directorProposalStatusLabel, directorRoomForModule, directorRoomPlan } from "./movieDirector";

describe("movie Director surface contracts", () => {
  it("maps Full Movie rooms to one contextual Director", () => {
    expect(directorRoomForModule("overview")).toBe("Overview");
    expect(directorRoomForModule("story")).toBe("Story");
    expect(directorRoomForModule("cast")).toBe("Cast");
    expect(directorRoomForModule("world")).toBe("World");
    expect(directorRoomForModule("scenes")).toBe("Scene");
    expect(directorRoomForModule("storyboard")).toBe("Storyboard");
    expect(directorRoomForModule("production")).toBe("Production");
  });

  it("keeps proposal and action language typed for review", () => {
    expect(directorActionTypeLabel("generate_shot")).toBe("Generate shot");
    expect(directorProposalStatusLabel("PendingApproval")).toBe("Review needed");
    expect(directorProposalStatusLabel("Approved")).toBe("Approved · ready to run");
    expect(directorActionTypeLabel("scene_planning")).toBe("Plan scene");
    expect(directorActionTypeLabel("storyboard_preparation")).toBe("Prepare storyboard context");
    expect(directorActionTypeLabel("production_readiness")).toBe("Review production readiness");
    expect(directorActionTypeLabel("edit_repair_audio")).toBe("Plan audio bridges");
  });

  it("makes Scenes room planning available at scene level and shot planning conditional on a ready shot", () => {
    const project = { guide: { lockedRevisionNumber: 1 } } as Parameters<typeof directorRoomPlan>[1];
    const scene = { id: "scene-1", shots: [] } as unknown as Parameters<typeof directorRoomPlan>[2];
    const plan = directorRoomPlan("Scene", project, scene, null);
    expect(plan.targetType).toBe("scene");
    expect(plan.validActions).toEqual(["scene_planning"]);

    const shot = { id: "shot-1", description: "Mara enters the harbor." } as Parameters<typeof directorRoomPlan>[3];
    expect(directorRoomPlan("Scene", project, scene, shot).validActions).toEqual(["scene_planning", "shot_planning"]);
  });

  it("keeps Cast room assistance project-scoped even when a scene has a shot", () => {
    const project = { guide: { lockedRevisionNumber: 1 } } as Parameters<typeof directorRoomPlan>[1];
    const scene = { id: "scene-1", shots: [] } as unknown as Parameters<typeof directorRoomPlan>[2];
    const shot = { id: "shot-1", description: "A real shot." } as Parameters<typeof directorRoomPlan>[3];
    const plan = directorRoomPlan("Cast", project, scene, shot);
    expect(plan.targetType).toBe("project");
    expect(plan.validActions).toEqual(["story_assistance"]);
  });

  it("keeps Storyboard and Production context provider-free", () => {
    const project = { guide: { lockedRevisionNumber: 1 } } as Parameters<typeof directorRoomPlan>[1];
    const scene = { id: "scene-1", shots: [] } as unknown as Parameters<typeof directorRoomPlan>[2];
    expect(directorRoomPlan("Storyboard", project, scene, null).validActions).toEqual(["storyboard_preparation"]);
    expect(directorRoomPlan("Production", project, scene, null).validActions).toEqual(["production_readiness"]);
  });

  it("uses safe, user-facing history labels", () => {
    expect(directorHistoryLabel("proposal_created")).toBe("Proposal created");
    expect(directorHistoryLabel("action_succeeded")).toBe("Action completed");
    expect(directorHistoryLabel("action_failed")).toBe("Action needs attention");
  });
});
