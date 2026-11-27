import { describe, expect, it } from "vitest";
import type { MovieMissingInsertDecision, MovieTakeSelectRecord } from "./api";
import { resolveInsertSelection } from "./movieInsertSelection";

function select(id: string, start: number, status = "Approved"): MovieTakeSelectRecord {
  return {
    id, movieTakeId: "take", selectNumber: 1, label: id, status, startMilliseconds: start,
    endMilliseconds: start + 1000, durationMilliseconds: 1000, notes: null, provenanceJson: "{}",
    createdByUserId: "editor", createdAt: "", updatedAt: "", reviewedByUserId: null, reviewedAt: null, reviewNote: null,
  };
}

function decision(fields: Partial<MovieMissingInsertDecision> = {}): MovieMissingInsertDecision {
  return {
    id: "decision", movieProjectId: "movie", proposalId: "proposal", gapId: "gap", timelineRevisionId: "revision",
    timelineRevisionNumber: 1, trackId: "track", beforeTimelineItemId: null, afterTimelineItemId: null,
    timelineInMilliseconds: 0, timelineOutMilliseconds: 1000, beforeShotId: null, afterShotId: null, sceneId: null,
    storySceneId: null, anchorShotId: null, approvedStoryRevisionId: null, lockedGuideRevisionId: null,
    productionKitHash: null, productionKitSchemaVersion: null, anchorTakeId: "take", anchorSelectId: null,
    selectedTakeId: null, selectedSelectId: null, contractVersion: "1", insertType: "insert", durationMilliseconds: 1000,
    description: "", purpose: "", proposalGroundingJson: "{}", continuityAnchorJson: "{}", screenDirectionAnchorJson: "{}",
    status: "PendingReview", createdByUserId: "editor", createdAt: "", reviewedByUserId: null, reviewedAt: null,
    reviewNote: null, appliedByUserId: null, appliedAt: null, appliedTimelineRevisionId: null, ...fields,
  };
}

const range = { sourceInMilliseconds: 4000, sourceOutMilliseconds: 5000 };
const first = select("first", 0);
const shown = select("shown", 4000);

describe("Movie Selects source range", () => {
  it("reviews the displayed interval when another approved select has the same duration", () => {
    const wrong = decision({ anchorSelectId: first.id });
    const right = decision({ id: "right", anchorSelectId: shown.id });
    const resolved = resolveInsertSelection([first, shown], "take", range, [wrong, right], "revision");
    expect(resolved.approved?.id).toBe(shown.id);
    expect(resolved.decision?.id).toBe(right.id);
    expect(resolveInsertSelection([first, shown], "take", range, [wrong], "revision").decision).toBeNull();
    expect(resolveInsertSelection([first], "take", range, [decision()], "revision").approved).toBeNull();
  });

  it("requires the reviewed take, select and revision before applying an approved decision", () => {
    const reviewed = decision({ status: "Approved", selectedTakeId: "take", selectedSelectId: shown.id });
    for (const mismatch of [
      { selectedSelectId: first.id }, { selectedSelectId: null }, { anchorSelectId: first.id },
      { selectedTakeId: "other" }, { timelineRevisionId: "older" }, { durationMilliseconds: 500 },
    ]) {
      expect(resolveInsertSelection([first, shown], "take", range, [{ ...reviewed, ...mismatch }], "revision").decision).toBeNull();
    }
    expect(resolveInsertSelection([first, shown], "take", range, [reviewed], "revision").decision).toBe(reviewed);
  });

  it("only offers a draft from the displayed interval for approval", () => {
    const other = select("other-draft", 0, "Draft");
    const draft = select("shown-draft", 4000, "Draft");
    const review = decision({ anchorSelectId: draft.id });
    const result = resolveInsertSelection([other, draft], "take", range, [review], "revision");
    expect(result.pending).toBe(draft);
    expect(result.approved).toBeNull();
    expect(result.decision).toBe(review);
  });
});
