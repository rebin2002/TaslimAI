import type { ActivityItem, Asset, Conversation, Project } from "@/lib/api";

export type HomeRecentKind = "project" | "conversation" | "activity" | "asset";

export type HomeRecentItem = {
  id: string;
  kind: HomeRecentKind;
  title: string;
  timestamp: string;
  href: string;
  status?: ActivityItem["status"];
  progressPercent?: number;
  projectType?: string;
  assetType?: Asset["assetType"];
  jobType?: ActivityItem["jobType"];
};

type HomeDashboardSources = {
  projects: Project[];
  conversations: Conversation[];
  activity: ActivityItem[];
  assets: Asset[];
};

function sortByMostRecent(items: HomeRecentItem[]) {
  return [...items].sort((left, right) => {
    const difference = new Date(right.timestamp).getTime() - new Date(left.timestamp).getTime();
    if (difference !== 0) return difference;
    return left.id.localeCompare(right.id);
  });
}

/**
 * Builds a unified, workspace-scoped continuation list only from records the
 * authenticated browser has already received through existing APIs.
 */
export function buildHomeRecentItems({ projects, conversations, activity, assets }: HomeDashboardSources, limit = 8): HomeRecentItem[] {
  const projectItems: HomeRecentItem[] = projects.map((project) => ({
    id: `project:${project.id}`,
    kind: "project",
    title: project.name,
    timestamp: project.updatedAt,
    href: `/projects/${project.id}`,
    projectType: project.type,
  }));

  const conversationItems: HomeRecentItem[] = conversations.map((conversation) => ({
    id: `conversation:${conversation.id}`,
    kind: "conversation",
    title: conversation.title,
    timestamp: conversation.lastMessageAt ?? conversation.updatedAt,
    href: `/chat/${conversation.id}`,
  }));

  const activityItems: HomeRecentItem[] = activity.map((item) => ({
    id: `activity:${item.jobId}`,
    kind: "activity",
    title: item.title,
    timestamp: item.completedAt ?? item.createdAt,
    href: item.assetId ? `/assets?search=${encodeURIComponent(item.title)}` : "/notifications",
    status: item.status,
    progressPercent: item.progressPercent,
    jobType: item.jobType,
  }));

  const assetItems: HomeRecentItem[] = assets.map((asset) => ({
    id: `asset:${asset.id}`,
    kind: "asset",
    title: asset.name,
    timestamp: asset.updatedAt,
    href: `/assets?search=${encodeURIComponent(asset.name)}`,
    assetType: asset.assetType,
  }));

  return sortByMostRecent([...projectItems, ...conversationItems, ...activityItems, ...assetItems]).slice(0, limit);
}

export function hasInFlightActivity(items: ActivityItem[]) {
  return items.some((item) => item.status === "Queued" || item.status === "Running");
}
