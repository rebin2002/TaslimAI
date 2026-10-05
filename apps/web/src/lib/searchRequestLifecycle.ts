export type RequestSequencer = {
  begin: () => number;
  isCurrent: (requestId: number) => boolean;
};

/**
 * Gives read-only views a small, dependency-free guard against late responses.
 * Starting any newer request, including an empty-query reset, invalidates older work.
 */
export function createRequestSequencer(): RequestSequencer {
  let currentRequestId = 0;
  return {
    begin: () => ++currentRequestId,
    isCurrent: (requestId) => requestId === currentRequestId,
  };
}
