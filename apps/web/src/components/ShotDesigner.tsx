"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import {
  Camera,
  Check,
  ChevronDown,
  LockKeyhole,
  Plus,
  ShieldCheck,
  Sparkles,
} from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import {
  type CinematographyIntentSelection,
  type CinematographyPreset,
  type MovieGuide,
  type MovieScene,
} from "@/lib/api";
import {
  CINEMATOGRAPHY_CONTROL_KEYS,
  CINEMATOGRAPHY_INTENTS,
  emptyCinematographySelection,
  guidePreset,
  parseCinematographyJson,
  selectionFromPreset,
  type CinematographyControlKey,
} from "@/lib/movieCinematography";
import type { Locale } from "@/lib/i18n";
import {
  formatShotDesignerNumber,
  formatShotDesignerSequence,
  shotDesignerCapabilityLabel,
  shotDesignerControlLabel,
  shotDesignerIntentLabel,
  shotDesignerText,
  shotDraftIssueText,
} from "@/lib/shotDesignerI18n";
import {
  issuesForField,
  parseShotDurationInput,
  shotDraftLimits,
  validateShotDraft,
  visibleShotDraftIssues,
  type ShotDraftIssue,
  type ShotDraftIssueField,
} from "@/lib/shotDraftValidation";

type ShotDesignerDraft = {
  description: string;
  durationSeconds: number | null;
  cinematography: CinematographyIntentSelection;
};

type ShotDesignerProps = {
  scene: MovieScene;
  guide: MovieGuide;
  presets: CinematographyPreset[];
  saving?: boolean;
  onAddShot: (draft: ShotDesignerDraft) => Promise<boolean>;
};

function blankDraft(preset?: CinematographyPreset): ShotDesignerDraft {
  return {
    description: "",
    durationSeconds: null,
    cinematography: preset
      ? selectionFromPreset(preset)
      : emptyCinematographySelection(),
  };
}

export function ShotDesigner({
  scene,
  guide,
  presets,
  saving = false,
  onAddShot,
}: ShotDesignerProps) {
  const { locale } = useLocale();
  const text = (key: Parameters<typeof shotDesignerText>[1], variables?: Record<string, string | number>) => shotDesignerText(locale, key, variables);
  const [mode, setMode] = useState<"simple" | "advanced">("simple");
  const [draft, setDraft] = useState<ShotDesignerDraft>(() =>
    blankDraft(presets.find((preset) => preset.intent === "natural")),
  );
  // The duration control keeps its own text: an entry the shot contract cannot
  // store must be named, not quietly dropped. The draft only ever holds a value
  // the API accepts, so no save path can submit a rejected duration.
  const [durationInput, setDurationInput] = useState("");
  const [submitAttempted, setSubmitAttempted] = useState(false);
  const [durationConfirmed, setDurationConfirmed] = useState(false);
  const didHydrate = useRef(Boolean(presets.length));
  const guideBible = guide.cinematographyBible;
  const guideBaseline = guidePreset(guideBible, presets);
  const selectedPreset = useMemo(
    () => presets.find((preset) => preset.id === draft.cinematography.presetId),
    [draft.cinematography.presetId, presets],
  );
  const guideIsLocked = guide.lockedRevisionNumber != null;

  const durationParse = useMemo(() => parseShotDurationInput(durationInput), [durationInput]);
  const draftIssues = useMemo(() => validateShotDraft(draft), [draft]);
  const issues = useMemo(
    () => [
      ...draftIssues.filter((issue) => issue.field !== "duration"),
      ...durationParse.issues,
    ],
    [draftIssues, durationParse],
  );
  const shownIssues = useMemo(
    () => visibleShotDraftIssues(issues, { submitted: submitAttempted, durationConfirmed }),
    [issues, submitAttempted, durationConfirmed],
  );
  // Advanced-only fields still gate the save, so when that panel is closed their
  // messages move to the footer: nothing can block the shot silently.
  const inlineFields = useMemo(
    () =>
      new Set<ShotDraftIssueField>([
        "description",
        "duration",
        ...(mode === "advanced"
          ? [...CINEMATOGRAPHY_CONTROL_KEYS, "continuityConstraints" as const]
          : []),
      ]),
    [mode],
  );
  const detachedIssues = shownIssues.filter((issue) => !inlineFields.has(issue.field));
  const issuesFor = (field: ShotDraftIssueField) => issuesForField(shownIssues, field);
  const issueId = (field: ShotDraftIssueField) => `shot-${field}-issues-${scene.id}`;

  function describedBy(field: ShotDraftIssueField, ...hints: string[]) {
    const ids = issuesFor(field).length ? [issueId(field), ...hints] : hints;
    return ids.length ? ids.join(" ") : undefined;
  }

  function invalid(field: ShotDraftIssueField) {
    return issuesFor(field).length ? true : undefined;
  }

  function updateDuration(raw: string) {
    const parsed = parseShotDurationInput(raw);
    setDurationInput(raw);
    setDraft((current) => ({
      ...current,
      durationSeconds: parsed.issues.length ? null : parsed.value,
    }));
  }

  useEffect(() => {
    if (didHydrate.current || !presets.length) return;
    didHydrate.current = true;
    setDraft((current) => ({
      ...current,
      cinematography: selectionFromPreset(
        presets.find((preset) => preset.intent === "natural") ?? presets[0],
        current.cinematography,
      ),
    }));
  }, [presets]);

  function chooseIntent(intent: string) {
    const preset = presets.find((item) => item.intent === intent);
    if (!preset) return;
    setDraft((current) => ({
      ...current,
      cinematography: selectionFromPreset(preset, {
        ...current.cinematography,
        notes: current.cinematography.notes,
      }),
    }));
  }

  function choosePreset(presetId: string) {
    const preset = presets.find((item) => item.id === presetId);
    if (preset)
      setDraft((current) => ({
        ...current,
        cinematography: selectionFromPreset(preset, current.cinematography),
      }));
  }

  function updateControl(key: CinematographyControlKey, value: string) {
    setDraft((current) => ({
      ...current,
      cinematography: { ...current.cinematography, [key]: value || null },
    }));
  }

  async function saveShot() {
    // Pressing "Add shot" is how a customer asks what is missing, so the click
    // is never swallowed: it reveals the messages and only then decides.
    setSubmitAttempted(true);
    if (saving || issues.length) return;
    const saved = await onAddShot({
      ...draft,
      description: draft.description.trim(),
      cinematography: {
        ...draft.cinematography,
        notes: draft.cinematography.notes?.trim() || null,
      },
    });
    if (!saved) return;
    setSubmitAttempted(false);
    setDurationConfirmed(false);
    setDurationInput("");
    setDraft((current) => ({
      ...blankDraft(
        selectedPreset ??
          presets.find(
            (preset) => preset.intent === current.cinematography.intent,
          ),
      ),
      cinematography: current.cinematography,
    }));
  }

  return (
    <section
      className="movie-shot-designer"
      aria-labelledby={`shot-designer-${scene.id}`}
    >
      <div className="movie-shot-designer-heading">
        <div className="movie-shot-title">
          <span className="movie-shot-icon">
            <Camera size={14} />
          </span>
          <div>
            <strong id={`shot-designer-${scene.id}`}>{text("title")}</strong>
            <small>{text("subtitle")}</small>
          </div>
        </div>
        <span className="movie-shot-saved-label">
          <span className="movie-live-dot" /> {text("savedToPlan")}
        </span>
      </div>

      <div
        className={`movie-guide-relationship ${guideIsLocked ? "is-locked" : ""}`}
      >
        <div className="movie-guide-relationship-icon">
          {guideIsLocked ? (
            <LockKeyhole size={14} />
          ) : (
            <ShieldCheck size={14} />
          )}
        </div>
        <div className="movie-guide-relationship-copy">
          <span>{text("guideBaseline")}</span>
          <strong>
            {guideBaseline
              ? `${shotDesignerIntentLabel(locale, guideBaseline.intent)} · ${guideBaseline.name}`
              : guideBible?.intent
                ? shotDesignerIntentLabel(locale, guideBible.intent)
                : text("noDirection")}
          </strong>
          <p>
            {guideBible?.notes ||
              guideBaseline?.summary ||
              text("shotOverrideHint")}
          </p>
        </div>
        <span className="movie-guide-relationship-status">
          {guideIsLocked
            ? text("lockedRevision", { revision: formatShotDesignerNumber(locale, guide.lockedRevisionNumber!) })
            : text("projectDirection")}
        </span>
      </div>

      <div
        className="movie-shot-mode-switch"
        role="tablist"
        aria-label={text("title")}
      >
        <button
          type="button"
          role="tab"
          aria-selected={mode === "simple"}
          className={mode === "simple" ? "is-active" : ""}
          onClick={() => setMode("simple")}
        >
          <Sparkles size={13} /> {text("simple")}
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={mode === "advanced"}
          className={mode === "advanced" ? "is-active" : ""}
          onClick={() => setMode("advanced")}
        >
          <ChevronDown size={13} /> {text("advanced")}
        </button>
      </div>

      <div className="movie-shot-intent-heading">
        <div>
          <span>{text("creativeIntent")}</span>
          <small>{text("creativeIntentHint")}</small>
        </div>
        <span className="movie-shot-override-note">{text("perShotOverride")}</span>
      </div>
      <div
        className="movie-shot-intents"
        role="radiogroup"
        aria-label={text("creativeIntent")}
      >
        {CINEMATOGRAPHY_INTENTS.map((intent) => {
          const preset = presets.find((item) => item.intent === intent);
          const selected = draft.cinematography.intent === intent;
          return (
            <button
              key={intent}
              type="button"
              role="radio"
              aria-checked={selected}
              className={selected ? "is-active" : ""}
              onClick={() => chooseIntent(intent)}
            >
              <span className="movie-intent-check">
                {selected && <Check size={11} />}
              </span>
              <strong>{shotDesignerIntentLabel(locale, intent)}</strong>
              <small>{preset?.summary ?? text("productionDirection")}</small>
            </button>
          );
        })}
      </div>

      <label className="movie-shot-field movie-shot-description">
        <span>{text("shotDescription")}</span>
        <textarea
          id={`shot-description-${scene.id}`}
          aria-invalid={invalid("description")}
          aria-describedby={describedBy("description")}
          value={draft.description}
          onChange={(event) =>
            setDraft((current) => ({
              ...current,
              description: event.target.value,
            }))
          }
          placeholder={text("shotDescriptionPlaceholder")}
          rows={2}
          maxLength={shotDraftLimits.descriptionCharacters}
        />
        <ShotDraftIssues
          locale={locale}
          issues={issuesFor("description")}
          id={issueId("description")}
        />
      </label>

      {mode === "simple" ? (
        <div className="movie-shot-simple-summary">
          <span className="movie-shot-summary-mark">
            <Check size={13} />
          </span>
          <div>
            <strong>
              {text("directionReady", {
                intent: selectedPreset?.name ?? shotDesignerIntentLabel(locale, draft.cinematography.intent),
              })}
            </strong>
            <p>
              {selectedPreset?.summary ??
                text("selectIntentHint")}
            </p>
            <small>
              {text("capabilityTruthExplicit", {
                legend: ["Native", "Translated", "Simulated/Post", "Unsupported"]
                  .map((item) => shotDesignerCapabilityLabel(locale, item))
                  .join(" · "),
              })}
            </small>
          </div>
          <button type="button" onClick={() => setMode("advanced")}>
            {text("tuneDetails")}
          </button>
        </div>
      ) : (
        <div className="movie-shot-advanced-panel">
          <div className="movie-shot-advanced-heading">
            <div>
              <span>{text("advancedControls")}</span>
              <small>{text("advancedControlsHint")}</small>
            </div>
            <label className="movie-shot-preset-field">
              <span>{text("preset")}</span>
              <select
                aria-label={text("preset")}
                value={draft.cinematography.presetId ?? "custom"}
                onChange={(event) => choosePreset(event.target.value)}
              >
                <option value="custom">{text("customDirection")}</option>
                {presets.map((preset) => (
                  <option key={preset.id} value={preset.id}>
                    {preset.name}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <div className="movie-shot-control-grid">
            {CINEMATOGRAPHY_CONTROL_KEYS.map((key) => (
              <label
                className={`movie-shot-field ${key === "compositionNotes" ? "movie-shot-wide" : ""}`}
                key={key}
              >
                <span>{shotDesignerControlLabel(locale, key)}</span>
                {key === "compositionNotes" ? (
                  <textarea
                    id={`shot-${key}-${scene.id}`}
                    aria-invalid={invalid(key)}
                    aria-describedby={describedBy(key)}
                    value={draft.cinematography[key] ?? ""}
                    onChange={(event) => updateControl(key, event.target.value)}
                    rows={2}
                  />
                ) : (
                  <input
                    id={`shot-${key}-${scene.id}`}
                    aria-invalid={invalid(key)}
                    aria-describedby={describedBy(key)}
                    value={draft.cinematography[key] ?? ""}
                    onChange={(event) => updateControl(key, event.target.value)}
                  />
                )}
                <ShotDraftIssues locale={locale} issues={issuesFor(key)} id={issueId(key)} />
              </label>
            ))}
          </div>
          <label className="movie-shot-field movie-shot-wide">
            <span>{text("continuityConstraints")}</span>
            <textarea
              id={`shot-continuityConstraints-${scene.id}`}
              aria-invalid={invalid("continuityConstraints")}
              aria-describedby={describedBy("continuityConstraints")}
              value={(draft.cinematography.continuityConstraints ?? []).join("\n")}
              onChange={(event) =>
                setDraft((current) => ({
                  ...current,
                  cinematography: {
                    ...current.cinematography,
                    continuityConstraints: event.target.value
                      .split("\n")
                      .map((item) => item.trim())
                      .filter(Boolean),
                  },
                }))
              }
              rows={3}
              placeholder={text("continuityConstraintsPlaceholder")}
            />
            <ShotDraftIssues
              locale={locale}
              issues={issuesFor("continuityConstraints")}
              id={issueId("continuityConstraints")}
            />
          </label>
          <CapabilityTruth locale={locale} selection={draft.cinematography} />
        </div>
      )}

      <div className="movie-shot-designer-footer">
        <label className="movie-shot-duration">
          <span>{text("duration")}</span>
          <input
            id={`shot-duration-${scene.id}`}
            type="number"
            min={shotDraftLimits.durationSecondsMinimum}
            max={shotDraftLimits.durationSecondsMaximum}
            step={1}
            inputMode="numeric"
            aria-invalid={invalid("duration")}
            aria-describedby={describedBy("duration", `shot-duration-hint-${scene.id}`)}
            value={durationInput}
            onChange={(event) => updateDuration(event.target.value)}
            onBlur={() => setDurationConfirmed(true)}
            placeholder="—"
          />
          <em>{text("seconds")}</em>
          <span className="movie-shot-duration-hint" id={`shot-duration-hint-${scene.id}`}>
            {text("durationHint", {
              minimum: formatShotDesignerNumber(
                locale,
                shotDraftLimits.durationSecondsMinimum,
              ),
              maximum: formatShotDesignerNumber(
                locale,
                shotDraftLimits.durationSecondsMaximum,
              ),
            })}
          </span>
          <ShotDraftIssues
            locale={locale}
            issues={issuesFor("duration")}
            id={issueId("duration")}
          />
        </label>
        <span className="movie-shot-save-note">
          {guideIsLocked ? text("guideLockedNote") : text("guideOverrideNote")}
        </span>
        <button
          type="button"
          className="movie-workspace-button is-primary"
          disabled={saving}
          onClick={() => void saveShot()}
        >
          <Plus size={13} /> {saving ? text("saving") : text("addShot")}
        </button>
      </div>

      <ShotDraftIssues
        locale={locale}
        issues={detachedIssues}
        id={`shot-detached-issues-${scene.id}`}
      />

      {scene.shots.length > 0 && (
        <div className="movie-shot-list" aria-label={text("savedShots")}>
          <div className="movie-shot-list-heading">
            <span>{text("savedShots")}</span>
            <small>{formatShotDesignerNumber(locale, scene.shots.length)}</small>
          </div>
          {scene.shots.map((shot) => {
            const selection = parseCinematographyJson(shot.cinematographyJson);
            return (
              <div className="movie-shot-list-item" key={shot.id}>
                <span>{formatShotDesignerSequence(locale, shot.sequence)}</span>
                <div>
                  <strong>
                    <bdi dir="auto">{shot.description}</bdi>
                  </strong>
                  <small>
                    {selection
                      ? `${shotDesignerIntentLabel(locale, selection.intent)} ${text("override")}${selection.presetId ? ` · ${presets.find((preset) => preset.id === selection.presetId)?.name ?? text("customDirection")}` : ""}`
                      : text("noCinematographyDirection")}
                  </small>
                </div>
              </div>
            );
          })}
        </div>
      )}
    </section>
  );
}

/**
 * Renders the localized reason for every invalid draft entry. It is a span so it
 * stays valid inside a `<label>`, and it announces itself because an entry can
 * turn invalid while the customer is still typing. A valid entry shows nothing.
 */
export function ShotDraftIssues({
  locale,
  issues,
  id,
}: {
  locale: Locale;
  issues: ShotDraftIssue[];
  id?: string;
}) {
  if (!issues.length) return null;
  return (
    <span id={id} className="movie-shot-issues" role="alert">
      {issues.map((issue, index) => (
        <span key={`${issue.field}-${issue.code}-${index}`} className="movie-shot-issue">
          {shotDraftIssueText(locale, issue)}
        </span>
      ))}
    </span>
  );
}

function CapabilityTruth({
  locale,
  selection,
}: {
  locale: Locale;
  selection: CinematographyIntentSelection;
}) {
  const references = selection.capabilityReferences ?? [];
  return (
    <div className="movie-capability-truth">
      <div className="movie-capability-heading">
        <div>
          <span>{shotDesignerText(locale, "capabilityClassification")}</span>
          <small>{shotDesignerText(locale, "capabilityClassificationHint")}</small>
        </div>
        <div className="movie-capability-legend">
          {(["Native", "Translated", "Simulated/Post", "Unsupported"] as const).map((item) => (
            <span key={item}>{shotDesignerCapabilityLabel(locale, item)}</span>
          ))}
        </div>
      </div>
      {references.length ? (
        <div className="movie-capability-list">
          {references.map((reference) => (
            <div
              key={reference.field}
              className={`movie-capability-item is-${reference.classification.toLowerCase().replace(/[^a-z]+/g, "-")}`}
            >
              <strong>
                {shotDesignerControlLabel(locale, reference.field as CinematographyControlKey) ??
                  reference.field}
              </strong>
              <span>{shotDesignerCapabilityLabel(locale, reference.classification)}</span>
              <small>
                {reference.rationale ||
                  shotDesignerText(locale, "capabilityTruthHint")}
              </small>
            </div>
          ))}
        </div>
      ) : (
        <p className="movie-capability-empty">
          {shotDesignerText(locale, "capabilityEmpty")}
        </p>
      )}
    </div>
  );
}

export type { ShotDesignerDraft };
