import { describe, expect, it } from "vitest";
import {
  issuesForField,
  parseShotDurationInput,
  shotDraftIssueIsImmediate,
  shotDraftLimits,
  shotDurationIssues,
  validateShotDraft,
  visibleShotDraftIssues,
  type ShotDraftValidationInput,
} from "./shotDraftValidation";
import type { CinematographyIntentSelection } from "./api";

/** Synthetic, provider-free drafts only: no project, asset, account, or network. */
function draft(
  overrides: Partial<ShotDraftValidationInput> = {},
): ShotDraftValidationInput {
  return {
    description: "Mara crosses into the morning light.",
    durationSeconds: 6,
    cinematography: { intent: "natural", shotSize: "Medium-wide" },
    ...overrides,
  };
}

function selection(
  overrides: Partial<CinematographyIntentSelection> = {},
): CinematographyIntentSelection {
  return { intent: "natural", ...overrides };
}

const text = (length: number) => "x".repeat(length);

describe("Movie Studio shot-edit draft validation", () => {
  it("keeps duration entries the shot API cannot store out of the draft", () => {
    // `DurationSeconds` is an `int?` on the server, so a fractional or unbounded
    // entry is a rejection, not a rounding opportunity. Each case must yield no
    // value at all so the payload cannot carry it.
    const cases: [string, "notWhole" | "notNumber" | "tooSmall" | "tooLarge"][] = [
      ["2.5", "notWhole"],
      ["0.5", "notWhole"],
      ["1e", "notNumber"],
      ["abc", "notNumber"],
      ["0", "tooSmall"],
      ["-4", "tooSmall"],
      ["3601", "tooLarge"],
      // A `type="number"` field accepts exponent syntax: `Number("1e999")` is
      // `Infinity`, which JSON serialises as `null`. The shot used to be saved
      // silently with no duration at all.
      ["1e999", "tooLarge"],
    ];
    for (const [raw, code] of cases) {
      const parsed = parseShotDurationInput(raw);
      expect(parsed.value, raw).toBeNull();
      expect(parsed.issues.map((issue) => issue.code), raw).toContain(code);
      expect(parsed.issues.every((issue) => issue.field === "duration"), raw).toBe(true);
    }
  });

  it("treats an empty duration as unset rather than invalid", () => {
    // The server policy returns no error for a null duration; the designer must
    // not invent one, or it would block every shot that leaves duration blank.
    for (const raw of ["", "   ", "\n"]) {
      expect(parseShotDurationInput(raw)).toEqual({ value: null, issues: [] });
    }
    expect(shotDurationIssues(null)).toEqual([]);
    expect(shotDurationIssues(undefined)).toEqual([]);
  });

  it("accepts the whole-number range the contract allows", () => {
    expect(parseShotDurationInput("6")).toEqual({ value: 6, issues: [] });
    expect(parseShotDurationInput(" 45 ")).toEqual({ value: 45, issues: [] });
    expect(parseShotDurationInput("1")).toEqual({ value: 1, issues: [] });
    expect(parseShotDurationInput("3600")).toEqual({ value: shotDraftLimits.durationSecondsMaximum, issues: [] });
    expect(validateShotDraft(draft({ durationSeconds: 3600 }))).toEqual([]);
    expect(validateShotDraft(draft({ durationSeconds: null }))).toEqual([]);
  });

  it("quotes the violated bound instead of saying the input is invalid", () => {
    expect(parseShotDurationInput("9999").issues).toEqual([
      { field: "duration", code: "tooLarge", limit: shotDraftLimits.durationSecondsMaximum },
    ]);
    expect(parseShotDurationInput("-1").issues).toEqual([
      { field: "duration", code: "tooSmall", limit: shotDraftLimits.durationSecondsMinimum },
    ]);
  });

  it("requires a description that will still mean something later", () => {
    expect(validateShotDraft(draft({ description: "" }))).toEqual([
      { field: "description", code: "required" },
    ]);
    expect(validateShotDraft(draft({ description: "   \n  " }))).toEqual([
      { field: "description", code: "required" },
    ]);
  });

  it("mirrors the server bounds for the production-intent fields", () => {
    // Bounds are copied from CinematographyIntentValidator: 600 per control,
    // 4,000 notes, 24 continuity lines of 800 characters each.
    expect(validateShotDraft(draft())).toEqual([]);
    expect(
      validateShotDraft(draft({ cinematography: selection({ shotSize: text(601) }) })),
    ).toEqual([
      { field: "shotSize", code: "tooLong", limit: shotDraftLimits.controlCharacters, characters: 601 },
    ]);
    expect(
      validateShotDraft(draft({ cinematography: selection({ cameraMovement: text(600) }) })),
    ).toEqual([]);
    expect(
      validateShotDraft(draft({ cinematography: selection({ notes: text(4001) }) })),
    ).toEqual([
      { field: "notes", code: "tooLong", limit: shotDraftLimits.notesCharacters, characters: 4001 },
    ]);
    expect(
      validateShotDraft(draft({ description: text(8001) })),
    ).toEqual([
      { field: "description", code: "tooLong", limit: shotDraftLimits.descriptionCharacters, characters: 8001 },
    ]);
  });

  it("counts continuity rules the way the contract does", () => {
    const lines = (count: number, length = 12) =>
      Array.from({ length: count }, (_, index) => `rule ${index} ${"y".repeat(length)}`);
    expect(
      validateShotDraft(draft({ cinematography: selection({ continuityConstraints: lines(24) }) })),
    ).toEqual([]);
    expect(
      validateShotDraft(draft({ cinematography: selection({ continuityConstraints: lines(25) }) })),
    ).toEqual([
      {
        field: "continuityConstraints",
        code: "tooManyLines",
        limit: shotDraftLimits.continuityLines,
        characters: 25,
      },
    ]);
    expect(
      validateShotDraft(
        draft({ cinematography: selection({ continuityConstraints: [text(801), "short rule"] }) }),
      ),
    ).toEqual([
      {
        field: "continuityConstraints",
        code: "tooLong",
        limit: shotDraftLimits.continuityLineCharacters,
        characters: 801,
      },
    ]);
  });

  it("reports every violated field at once so nothing stays silently blocked", () => {
    const issues = validateShotDraft(
      draft({
        description: "",
        durationSeconds: 2.5,
        cinematography: selection({ lighting: text(700), continuityConstraints: ["a".repeat(900)] }),
      }),
    );
    expect(issues.map((issue) => issue.field)).toEqual([
      "description",
      "duration",
      "lighting",
      "continuityConstraints",
    ]);
    expect(issuesForField(issues, "duration")).toEqual([
      { field: "duration", code: "notWhole" },
    ]);
  });

  it("shows an over-length entry while typing and a missing entry only on save", () => {
    const [required] = validateShotDraft(draft({ description: "" }));
    const [tooLarge] = shotDurationIssues(4000);
    const [notNumber] = shotDurationIssues(Number.NaN);
    expect(shotDraftIssueIsImmediate(required)).toBe(false);
    expect(shotDraftIssueIsImmediate(tooLarge)).toBe(true);
    // A half-typed exponent is not a number *yet*; flashing that mid-keystroke
    // is noise, so it waits for the field to be left or for a save attempt.
    expect(shotDraftIssueIsImmediate(notNumber)).toBe(false);

    const issues = [required, tooLarge, notNumber];
    expect(visibleShotDraftIssues(issues, { submitted: false, durationConfirmed: false })).toEqual([
      tooLarge,
    ]);
    expect(visibleShotDraftIssues(issues, { submitted: false, durationConfirmed: true })).toEqual([
      tooLarge,
      notNumber,
    ]);
    expect(visibleShotDraftIssues(issues, { submitted: true, durationConfirmed: false })).toEqual(issues);
  });
});
