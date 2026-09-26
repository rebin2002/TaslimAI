export const directorRooms = ["Story", "Cast", "World", "Scene", "Shot", "Storyboard", "Production"] as const;

export type DirectorRoom = (typeof directorRooms)[number];

export function directorRoomForModule(module: string): DirectorRoom {
  switch (module) {
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
