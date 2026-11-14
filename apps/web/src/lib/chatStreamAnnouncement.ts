export type ChatStreamAnnouncement = "idle" | "generating" | "completed" | "failed" | "cancelled";

export function chatStreamAnnouncementKey(status: ChatStreamAnnouncement) {
  if (status === "idle") return null;
  return `chat.stream${status[0].toUpperCase()}${status.slice(1)}`;
}
