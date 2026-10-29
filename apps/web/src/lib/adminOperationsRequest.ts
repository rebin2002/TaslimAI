export type AdminOperationsRange = { fromUtc: string; toUtc: string };

export type AdminOperationsRequestToken = AdminOperationsRange & { sequence: number };

export function startAdminOperationsRequest(sequence: number, range: AdminOperationsRange): AdminOperationsRequestToken {
  return { sequence: sequence + 1, ...range };
}

export function isAdminOperationsRequestCurrent(
  token: AdminOperationsRequestToken,
  currentSequence: number,
  activeRange: AdminOperationsRange,
): boolean {
  return token.sequence === currentSequence
    && token.fromUtc === activeRange.fromUtc
    && token.toUtc === activeRange.toUtc;
}
