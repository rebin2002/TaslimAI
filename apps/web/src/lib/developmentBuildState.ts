export type DevelopmentBuildState = {
  version: 1;
  completedStepIds: string[];
  note: string;
  updatedAt: string;
};

const storagePrefix = "taslim:development-build:";
const maxSteps = 12;
const maxNoteLength = 2_000;

export function developmentBuildStorageKey(workspaceId: string) {
  return `${storagePrefix}${workspaceId}`;
}

export function createDefaultDevelopmentBuildState(): DevelopmentBuildState {
  return {
    version: 1,
    completedStepIds: [],
    note: "",
    updatedAt: new Date(0).toISOString(),
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function normalizeStepIds(value: unknown) {
  if (!Array.isArray(value)) return [];
  return [...new Set(value
    .filter((item): item is string => typeof item === "string" && item.trim().length > 0)
    .map((item) => item.trim()))].slice(0, maxSteps);
}

function normalizeState(value: unknown): DevelopmentBuildState {
  const fallback = createDefaultDevelopmentBuildState();
  if (!isRecord(value) || value.version !== 1) return fallback;
  const note = typeof value.note === "string" ? value.note.slice(0, maxNoteLength) : fallback.note;
  const updatedAt = typeof value.updatedAt === "string" && value.updatedAt.length <= 40
    ? value.updatedAt
    : fallback.updatedAt;
  return {
    version: 1,
    completedStepIds: normalizeStepIds(value.completedStepIds),
    note,
    updatedAt,
  };
}

export function readDevelopmentBuildState(workspaceId: string): DevelopmentBuildState {
  const fallback = createDefaultDevelopmentBuildState();
  if (typeof window === "undefined" || !workspaceId.trim()) return fallback;
  try {
    const stored = window.localStorage.getItem(developmentBuildStorageKey(workspaceId));
    return stored ? normalizeState(JSON.parse(stored) as unknown) : fallback;
  } catch {
    return fallback;
  }
}

export function saveDevelopmentBuildState(workspaceId: string, state: DevelopmentBuildState): boolean {
  if (typeof window === "undefined" || !workspaceId.trim()) return false;
  try {
    window.localStorage.setItem(
      developmentBuildStorageKey(workspaceId),
      JSON.stringify(normalizeState(state)),
    );
    return true;
  } catch {
    return false;
  }
}

export function setDevelopmentBuildStepCompletion(
  state: DevelopmentBuildState,
  stepId: string,
  completed: boolean,
): DevelopmentBuildState {
  const normalizedId = stepId.trim();
  if (!normalizedId) return state;
  const completedStepIds = new Set(state.completedStepIds);
  if (completed) completedStepIds.add(normalizedId);
  else completedStepIds.delete(normalizedId);
  return {
    ...state,
    completedStepIds: [...completedStepIds].slice(0, maxSteps),
    updatedAt: new Date().toISOString(),
  };
}

export function setDevelopmentBuildNote(state: DevelopmentBuildState, note: string): DevelopmentBuildState {
  return {
    ...state,
    note: note.slice(0, maxNoteLength),
    updatedAt: new Date().toISOString(),
  };
}

export function isDevelopmentBuildStepComplete(state: DevelopmentBuildState, stepId: string) {
  return state.completedStepIds.includes(stepId);
}
