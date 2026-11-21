export type UsageLoadState = {
  errorKey: string | null;
  attempt: number;
};

export const initialUsageLoadState: UsageLoadState = { errorKey: null, attempt: 0 };

export function usageRequestKey(workspaceId: string | null | undefined, page: number) {
  return `${workspaceId ?? ""}:${page}`;
}

export function failUsageLoad(state: UsageLoadState, requestKey: string): UsageLoadState {
  return { ...state, errorKey: requestKey };
}

export function retryUsageLoad(state: UsageLoadState): UsageLoadState {
  return { errorKey: null, attempt: state.attempt + 1 };
}

export function hasUsageLoadError(state: UsageLoadState, requestKey: string) {
  return state.errorKey === requestKey;
}
