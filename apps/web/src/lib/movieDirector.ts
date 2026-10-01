import type { MovieProject, MovieScene, MovieShot } from "./api";

export const directorRooms = ["Overview", "Story", "Cast", "World", "Scene", "Shot", "Storyboard", "Production"] as const;

export type DirectorRoom = (typeof directorRooms)[number];
export type DirectorRoomAction = "scene_planning" | "shot_planning" | "storyboard_preparation" | "production_readiness" | "project_readiness" | "story_assistance";
export type DirectorRoomPrerequisite = { key: string; label: string; satisfied: boolean; detail: string };
export type DirectorRoomPlan = { prerequisites: DirectorRoomPrerequisite[]; validActions: DirectorRoomAction[]; targetType: "project" | "scene" | "shot" };

export function directorRoomForModule(module: string): DirectorRoom {
  switch (module) {
    case "overview":
      return "Overview";
    case "story":
      return "Story";
    case "cast":
      return "Cast";
    case "world":
      return "World";
    case "storyboard":
      return "Storyboard";
    case "production":
      return "Production";
    case "selects":
      return "Production";
    case "scenes":
      return "Scene";
    default:
      return "Story";
  }
}

export function directorActionTypeLabel(actionType: string) {
  switch (actionType) {
    case "generate_shot":
      return "Generate shot";
    case "propose_shots":
      return "Apply shot plan";
    case "regenerate_shots":
      return "Apply regenerated shot plan";
    case "story_assistance":
      return "Cast continuity assistance";
    case "scene_planning":
      return "Plan scene";
    case "shot_planning":
      return "Plan shot";
    case "storyboard_preparation":
      return "Prepare storyboard context";
    case "production_readiness":
      return "Review production readiness";
    case "project_readiness":
      return "Review project readiness";
    default:
      return "Director action";
  }
}

export function directorProposalStatusLabel(status: string) {
  switch (status) {
    case "PendingApproval":
      return "Review needed";
    case "Approved":
      return "Approved · ready to run";
    case "Rejected":
      return "Rejected";
    case "Expired":
      return "Expired";
    default:
      return status;
  }
}

export function directorHistoryLabel(eventType: string) {
  switch (eventType) {
    case "context_assembled":
      return "Context assembled";
    case "proposal_created":
      return "Proposal created";
    case "proposal_approved":
      return "Proposal approved";
    case "proposal_rejected":
      return "Proposal rejected";
    case "action_ready":
      return "Action ready";
    case "action_started":
      return "Action started";
    case "action_succeeded":
      return "Action completed";
    case "action_failed":
      return "Action needs attention";
    default:
      return "Director update";
  }
}

export function directorRoomPlan(room: DirectorRoom, project: MovieProject, selectedScene: MovieScene | null, selectedShot: MovieShot | null): DirectorRoomPlan {
  const guideLocked = Boolean(project.guide.lockedRevisionNumber);
  const sceneSelected = Boolean(selectedScene);
  const shotSelected = Boolean(selectedShot);
  const shotPlanReady = Boolean(selectedShot?.description.trim());
  const prerequisites: DirectorRoomPrerequisite[] = [
    { key: "guide_locked", label: "Movie Guide locked", satisfied: guideLocked, detail: guideLocked ? "Authoritative guide rules are available." : "Lock the current Movie Guide first." },
    { key: "scene_selected", label: "Scene selected", satisfied: sceneSelected, detail: sceneSelected ? "The Director is scoped to the selected scene." : "Select a scene from the production map." },
    { key: "shot_selected", label: "Shot selected", satisfied: shotSelected, detail: shotSelected ? "The Director can target this shot." : "Select a shot when the room action is shot-specific." },
    { key: "shot_plan_ready", label: "Shot plan ready", satisfied: shotPlanReady, detail: shotPlanReady ? "The selected shot has a usable plan." : "Add the missing shot-plan detail before asking for shot planning." },
  ];
  let validActions: DirectorRoomAction[] = [];
  let targetType: DirectorRoomPlan["targetType"] = selectedShot ? "shot" : selectedScene ? "scene" : "project";
  if (room === "Cast") {
    validActions = guideLocked ? ["story_assistance"] : [];
    targetType = "project";
  } else if (room === "Scene") {
    validActions = guideLocked && sceneSelected ? (["scene_planning", ...(shotSelected && shotPlanReady ? ["shot_planning"] : [])] as DirectorRoomAction[]) : [];
  } else if (room === "Storyboard") {
    validActions = guideLocked && (sceneSelected || shotSelected) ? ["storyboard_preparation"] : [];
  } else if (room === "Production") {
    validActions = guideLocked && (sceneSelected || shotSelected) ? ["production_readiness"] : [];
  } else if (room === "Story" || room === "Overview") {
    validActions = guideLocked ? ["project_readiness"] : [];
    targetType = "project";
  } else if ((room === "World" || room === "Shot") && guideLocked && shotSelected && shotPlanReady) {
    validActions = ["shot_planning"];
  }
  return { prerequisites, validActions, targetType };
}
