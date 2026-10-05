export type LearningTrackId = "foundations" | "frontend" | "backend";

export type DevelopmentLearningState = {
  version: 1;
  activeTrack: LearningTrackId;
  completedLessonIds: string[];
  notes: string;
  updatedAt: string;
};

const storagePrefix = "taslim:development-learning:";
const maxLessons = 60;
const maxNotesLength = 2_000;
const validTracks = new Set<LearningTrackId>(["foundations", "frontend", "backend"]);

export function developmentLearningStorageKey(workspaceId: string) {
  return `${storagePrefix}${workspaceId}`;
}

export function createDefaultDevelopmentLearningState(): DevelopmentLearningState {
  return {
    version: 1,
    activeTrack: "foundations",
    completedLessonIds: [],
    notes: "",
    updatedAt: new Date(0).toISOString(),
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function normalizeLessonIds(value: unknown) {
  if (!Array.isArray(value)) return [];
  return [...new Set(value.filter((item): item is string => typeof item === "string" && item.trim().length > 0).map((item) => item.trim()))].slice(0, maxLessons);
}

function normalizeState(value: unknown): DevelopmentLearningState {
  const fallback = createDefaultDevelopmentLearningState();
  if (!isRecord(value) || value.version !== 1) return fallback;
  const activeTrack = typeof value.activeTrack === "string" && validTracks.has(value.activeTrack as LearningTrackId)
    ? value.activeTrack as LearningTrackId
    : fallback.activeTrack;
  const notes = typeof value.notes === "string" ? value.notes.slice(0, maxNotesLength) : fallback.notes;
  const updatedAt = typeof value.updatedAt === "string" && value.updatedAt.length <= 40 ? value.updatedAt : fallback.updatedAt;
  return { version: 1, activeTrack, completedLessonIds: normalizeLessonIds(value.completedLessonIds), notes, updatedAt };
}

export function readDevelopmentLearningState(workspaceId: string): DevelopmentLearningState {
  const fallback = createDefaultDevelopmentLearningState();
  if (typeof window === "undefined" || !workspaceId.trim()) return fallback;
  try {
    const stored = window.localStorage.getItem(developmentLearningStorageKey(workspaceId));
    return stored ? normalizeState(JSON.parse(stored) as unknown) : fallback;
  } catch {
    return fallback;
  }
}

export function saveDevelopmentLearningState(workspaceId: string, state: DevelopmentLearningState): boolean {
  if (typeof window === "undefined" || !workspaceId.trim()) return false;
  try {
    window.localStorage.setItem(developmentLearningStorageKey(workspaceId), JSON.stringify(normalizeState(state)));
    return true;
  } catch {
    return false;
  }
}

export function setDevelopmentLessonCompletion(state: DevelopmentLearningState, lessonId: string, completed: boolean): DevelopmentLearningState {
  const normalizedId = lessonId.trim();
  if (!normalizedId) return state;
  const completedLessonIds = new Set(state.completedLessonIds);
  if (completed) completedLessonIds.add(normalizedId);
  else completedLessonIds.delete(normalizedId);
  return { ...state, completedLessonIds: [...completedLessonIds].slice(0, maxLessons), updatedAt: new Date().toISOString() };
}

export function setDevelopmentLearningNotes(state: DevelopmentLearningState, notes: string): DevelopmentLearningState {
  return { ...state, notes: notes.slice(0, maxNotesLength), updatedAt: new Date().toISOString() };
}

export function isDevelopmentLessonComplete(state: DevelopmentLearningState, lessonId: string) {
  return state.completedLessonIds.includes(lessonId);
}
