export type RequestHandle = {
  signal: AbortSignal;
  isCurrent: () => boolean;
};

type ActiveRequest = {
  controller: AbortController;
  generation: number;
};

export function isAbortError(error: unknown): boolean {
  return typeof error === "object" && error !== null && "name" in error && error.name === "AbortError";
}

export function createRequestGuard() {
  let generation = 0;
  let active: ActiveRequest | null = null;

  function begin(): RequestHandle {
    active?.controller.abort();
    const next: ActiveRequest = { controller: new AbortController(), generation: generation + 1 };
    generation = next.generation;
    active = next;
    return {
      signal: next.controller.signal,
      isCurrent: () => active === next && !next.controller.signal.aborted,
    };
  }

  function cancel() {
    generation += 1;
    active?.controller.abort();
    active = null;
  }

  return { begin, cancel };
}
