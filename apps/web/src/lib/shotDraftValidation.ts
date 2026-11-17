import type { CinematographyIntentSelection } from "./api";
import { CINEMATOGRAPHY_CONTROL_KEYS, type CinematographyControlKey } from "./movieCinematography";

/**
 * Client-side mirror of the shot-creation contract that
 * `POST /api/movie-studio/scenes/{sceneId}/shots` enforces, so the Shot Designer
 * can stop an unsalvageable draft and say *why* instead of paying a round trip
 * for a `400 MOVIE_SHOT_INVALID` (or, worse, a deserialization fault) after the
 * customer has already pressed "Add shot".
 *
 * Bounds are copied from the server policy, never invented here:
 * - `MovieDurationBudgetPolicy.MaxShotDurationSeconds` / its "greater than zero"
 *   rule and the `int?` duration type (whole seconds only).
 * - `CinematographyIntentValidator.MaxFieldLength` (600) and `MaxNotesLength`
 *   (4,000), plus its 24-line / 800-character continuity-constraint limits.
 * - The shot description column bound (8,000 characters) that the designer
 *   already applies to its textarea.
 *
 * Nothing in this module renders, fetches, or calls a provider: a draft is
 * validated as pure data, which keeps these rules deterministic in tests.
 */
export const shotDraftLimits = {
  descriptionCharacters: 8_000,
  durationSecondsMinimum: 1,
  durationSecondsMaximum: 3_600,
  controlCharacters: 600,
  notesCharacters: 4_000,
  continuityLines: 24,
  continuityLineCharacters: 800,
} as const;

export type ShotDraftIssueField =
  | "description"
  | "duration"
  | "notes"
  | "continuityConstraints"
  | CinematographyControlKey;

export type ShotDraftIssueCode =
  | "required"
  | "tooLong"
  | "notNumber"
  | "notWhole"
  | "tooSmall"
  | "tooLarge"
  | "tooManyLines";

export type ShotDraftIssue = {
  field: ShotDraftIssueField;
  code: ShotDraftIssueCode;
  /** The bound that was violated, in the unit named by the message. */
  limit?: number;
  /** The offending length, so a message can quote what was actually entered. */
  characters?: number;
};

export type ShotDraftValidationInput = {
  description: string;
  durationSeconds: number | null;
  cinematography?: CinematographyIntentSelection | null;
};

/**
 * A missing or blank value is never an invalid one: the server accepts a shot
 * with no duration and treats it as unset, so only a typed value is validated.
 */
export function shotDurationIssues(value: number | null | undefined): ShotDraftIssue[] {
  if (value === null || value === undefined) return [];
  if (Number.isNaN(value)) return [{ field: "duration", code: "notNumber" }];
  // An exponent entry such as `1e999` is a real `type="number"` value: it is
  // infinite rather than out of scale, and the sign says which bound to name.
  if (!Number.isFinite(value)) {
    return [
      value > 0
        ? { field: "duration", code: "tooLarge", limit: shotDraftLimits.durationSecondsMaximum }
        : { field: "duration", code: "tooSmall", limit: shotDraftLimits.durationSecondsMinimum },
    ];
  }
  // `int? DurationSeconds` rejects a fractional body outright, so the client must
  // not offer one - and "a whole number of seconds" is the actionable answer for
  // `0.5`, where a minimum-seconds message would only send the customer astray.
  if (!Number.isInteger(value)) return [{ field: "duration", code: "notWhole" }];
  if (value < shotDraftLimits.durationSecondsMinimum) {
    return [{ field: "duration", code: "tooSmall", limit: shotDraftLimits.durationSecondsMinimum }];
  }
  if (value > shotDraftLimits.durationSecondsMaximum) {
    return [{ field: "duration", code: "tooLarge", limit: shotDraftLimits.durationSecondsMaximum }];
  }
  return [];
}

/**
 * Parses the raw text of the duration control. An unusable entry yields no value
 * at all, so a draft can never carry a duration the API would reject; the issue
 * list is what tells the customer why their number was not kept.
 *
 * `Number.isFinite` matters here: a HTML `type="number"` field accepts the
 * exponent syntax `1e999`, which becomes `Infinity` and then serialises to JSON
 * `null` - today that silently saves the shot with no duration at all.
 */
export function parseShotDurationInput(raw: string): {
  value: number | null;
  issues: ShotDraftIssue[];
} {
  const trimmed = raw.trim();
  if (!trimmed) return { value: null, issues: [] };
  const value = Number(trimmed);
  if (Number.isNaN(value)) return { value: null, issues: [{ field: "duration", code: "notNumber" }] };
  const issues = shotDurationIssues(value);
  return { value: issues.length ? null : value, issues };
}

function textIssues(
  field: ShotDraftIssueField,
  value: string | null | undefined,
  limit: number,
): ShotDraftIssue[] {
  const length = value?.trim().length ?? 0;
  return length > limit ? [{ field, code: "tooLong", limit, characters: length }] : [];
}

/** Validates the stored draft: description, duration, and every production-intent field. */
export function validateShotDraft(draft: ShotDraftValidationInput): ShotDraftIssue[] {
  const issues: ShotDraftIssue[] = [];
  const description = draft.description.trim();
  if (!description) issues.push({ field: "description", code: "required" });
  issues.push(...textIssues("description", draft.description, shotDraftLimits.descriptionCharacters));
  issues.push(...shotDurationIssues(draft.durationSeconds));

  const selection = draft.cinematography ?? null;
  if (selection) {
    issues.push(...textIssues("notes", selection.notes, shotDraftLimits.notesCharacters));
    for (const key of CINEMATOGRAPHY_CONTROL_KEYS) {
      issues.push(...textIssues(key, selection[key], shotDraftLimits.controlCharacters));
    }
    const constraints = selection.continuityConstraints ?? [];
    if (constraints.length > shotDraftLimits.continuityLines) {
      issues.push({
        field: "continuityConstraints",
        code: "tooManyLines",
        limit: shotDraftLimits.continuityLines,
        characters: constraints.length,
      });
    }
    const longest = constraints.reduce(
      (longest, item) => Math.max(longest, item.trim().length),
      0,
    );
    if (longest > shotDraftLimits.continuityLineCharacters) {
      issues.push({
        field: "continuityConstraints",
        code: "tooLong",
        limit: shotDraftLimits.continuityLineCharacters,
        characters: longest,
      });
    }
  }
  return issues;
}

export function issuesForField(
  issues: ShotDraftIssue[],
  field: ShotDraftIssueField,
): ShotDraftIssue[] {
  return issues.filter((issue) => issue.field === field);
}

/**
 * "Something is required" only makes sense once the customer has tried to save;
 * an out-of-range or over-length entry is unambiguous while it is being typed.
 * `notNumber` is the one range-family exception, because a half-typed exponent
 * (`1e`) is not a number yet and would otherwise flash a fault mid-keystroke.
 */
export function shotDraftIssueIsImmediate(issue: ShotDraftIssue): boolean {
  return issue.code !== "required" && issue.code !== "notNumber";
}

export function visibleShotDraftIssues(
  issues: ShotDraftIssue[],
  state: { submitted: boolean; durationConfirmed: boolean },
): ShotDraftIssue[] {
  return issues.filter((issue) => {
    if (shotDraftIssueIsImmediate(issue)) return true;
    if (issue.code === "notNumber") {
      return state.submitted || state.durationConfirmed;
    }
    return state.submitted;
  });
}
