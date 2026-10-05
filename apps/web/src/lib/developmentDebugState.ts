export const DEVELOPMENT_DEBUG_MAX_NOTES = 4_000;

export const developmentDebugSteps = [
  "reproduce",
  "compare",
  "boundary",
  "observe",
  "record",
] as const;

export type DevelopmentDebugStep = (typeof developmentDebugSteps)[number];
export type DevelopmentDebugState = {
  completed: Record<DevelopmentDebugStep, boolean>;
  notes: string;
};

type StorageLike = Pick<Storage, "getItem" | "setItem" | "removeItem">;

const STORAGE_PREFIX = "taslim-development-debug";

export function emptyDevelopmentDebugState(): DevelopmentDebugState {
  return {
    completed: {
      reproduce: false,
      compare: false,
      boundary: false,
      observe: false,
      record: false,
    },
    notes: "",
  };
}

export function developmentDebugStorageKey(workspaceId: string): string {
  return `${STORAGE_PREFIX}:${encodeURIComponent(workspaceId)}`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

export function parseDevelopmentDebugState(raw: string | null): DevelopmentDebugState {
  const fallback = emptyDevelopmentDebugState();
  if (!raw) return fallback;

  try {
    const parsed: unknown = JSON.parse(raw);
    if (!isRecord(parsed)) return fallback;

    const completedValue = isRecord(parsed.completed) ? parsed.completed : {};
    const completed = { ...fallback.completed };
    for (const step of developmentDebugSteps) {
      completed[step] = completedValue[step] === true;
    }

    return {
      completed,
      notes: typeof parsed.notes === "string" ? parsed.notes.slice(0, DEVELOPMENT_DEBUG_MAX_NOTES) : "",
    };
  } catch {
    return fallback;
  }
}

export function serializeDevelopmentDebugState(state: DevelopmentDebugState): string {
  return JSON.stringify({
    completed: Object.fromEntries(developmentDebugSteps.map((step) => [step, state.completed[step] === true])),
    notes: state.notes.slice(0, DEVELOPMENT_DEBUG_MAX_NOTES),
  });
}

export function loadDevelopmentDebugState(workspaceId: string, storage?: StorageLike): DevelopmentDebugState {
  if (!storage) return emptyDevelopmentDebugState();
  try {
    return parseDevelopmentDebugState(storage.getItem(developmentDebugStorageKey(workspaceId)));
  } catch {
    return emptyDevelopmentDebugState();
  }
}

export function persistDevelopmentDebugState(workspaceId: string, state: DevelopmentDebugState, storage?: StorageLike): void {
  if (!storage) return;
  try {
    storage.setItem(developmentDebugStorageKey(workspaceId), serializeDevelopmentDebugState(state));
  } catch {
    // Browser storage can be disabled or full; the in-memory guide remains usable.
  }
}

export function clearDevelopmentDebugState(workspaceId: string, storage?: StorageLike): void {
  if (!storage) return;
  try {
    storage.removeItem(developmentDebugStorageKey(workspaceId));
  } catch {
    // Clearing is best effort and must not break the guide.
  }
}

export function developmentDebugProgress(state: DevelopmentDebugState): { completed: number; total: number } {
  return {
    completed: developmentDebugSteps.filter((step) => state.completed[step]).length,
    total: developmentDebugSteps.length,
  };
}
