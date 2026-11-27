import type { MovieMissingInsertDecision, MovieTakeSelectRecord } from "./api";

export type MovieSourceRange = { sourceInMilliseconds: number; sourceOutMilliseconds: number };

export function selectsForRange(records: MovieTakeSelectRecord[], takeId: string, range: MovieSourceRange) {
  return records.filter((record) =>
    record.movieTakeId === takeId &&
    record.startMilliseconds === range.sourceInMilliseconds &&
    record.endMilliseconds === range.sourceOutMilliseconds,
  );
}

// Only pair decisions with a persisted record for the range currently displayed.
export function resolveInsertSelection(
  records: MovieTakeSelectRecord[],
  takeId: string,
  range: MovieSourceRange,
  decisions: MovieMissingInsertDecision[],
  revisionId: string | null | undefined,
) {
  const matching = selectsForRange(records, takeId, range);
  const approved = matching.filter((record) => record.status === "Approved");
  const drafts = matching.filter((record) => record.status === "Draft");
  const pending = drafts[0] ?? null;
  for (const decision of decisions) {
    if (decision.timelineRevisionId !== revisionId || decision.anchorTakeId !== takeId) continue;
    if (decision.status !== "PendingReview" && decision.status !== "Approved") continue;
    const select = approved.find((record) =>
      record.durationMilliseconds === decision.durationMilliseconds &&
      (!decision.anchorSelectId || decision.anchorSelectId === record.id) &&
      (!decision.selectedSelectId || decision.selectedSelectId === record.id) &&
      (decision.status !== "Approved" || (decision.selectedTakeId === takeId && decision.selectedSelectId === record.id)),
    );
    if (select) return { approved: select, pending, decision };
    // A pending range can be reviewed before the decision is approved.
    if (decision.status === "PendingReview" && !decision.selectedSelectId) {
      const draft = drafts.find((record) => record.durationMilliseconds === decision.durationMilliseconds &&
        (!decision.anchorSelectId || decision.anchorSelectId === record.id));
      if (draft) return { approved: null, pending: draft, decision };
    }
  }
  return { approved: approved[0] ?? null, pending, decision: null };
}
