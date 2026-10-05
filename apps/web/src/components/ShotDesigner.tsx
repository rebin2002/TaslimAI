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
} from "@/lib/shotDesignerI18n";

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
  const didHydrate = useRef(Boolean(presets.length));
  const guideBible = guide.cinematographyBible;
  const guideBaseline = guidePreset(guideBible, presets);
  const selectedPreset = useMemo(
    () => presets.find((preset) => preset.id === draft.cinematography.presetId),
    [draft.cinematography.presetId, presets],
  );
  const guideIsLocked = guide.lockedRevisionNumber != null;

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
    if (!draft.description.trim() || saving) return;
    const saved = await onAddShot({
      ...draft,
      description: draft.description.trim(),
      cinematography: {
        ...draft.cinematography,
        notes: draft.cinematography.notes?.trim() || null,
      },
    });
    if (!saved) return;
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
          value={draft.description}
          onChange={(event) =>
            setDraft((current) => ({
              ...current,
              description: event.target.value,
            }))
          }
          placeholder={text("shotDescriptionPlaceholder")}
          rows={2}
          maxLength={8000}
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
                    value={draft.cinematography[key] ?? ""}
                    onChange={(event) => updateControl(key, event.target.value)}
                    rows={2}
                  />
                ) : (
                  <input
                    value={draft.cinematography[key] ?? ""}
                    onChange={(event) => updateControl(key, event.target.value)}
                  />
                )}
              </label>
            ))}
          </div>
          <label className="movie-shot-field movie-shot-wide">
            <span>{text("continuityConstraints")}</span>
            <textarea
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
          </label>
          <CapabilityTruth locale={locale} selection={draft.cinematography} />
        </div>
      )}

      <div className="movie-shot-designer-footer">
        <label className="movie-shot-duration">
          <span>{text("duration")}</span>
          <input
            type="number"
            min={1}
            max={3600}
            value={draft.durationSeconds ?? ""}
            onChange={(event) =>
              setDraft((current) => ({
                ...current,
                durationSeconds: event.target.value
                  ? Number(event.target.value)
                  : null,
              }))
            }
            placeholder="—"
          />
          <em>{text("seconds")}</em>
        </label>
        <span className="movie-shot-save-note">
          {guideIsLocked ? text("guideLockedNote") : text("guideOverrideNote")}
        </span>
        <button
          type="button"
          className="movie-workspace-button is-primary"
          disabled={saving || !draft.description.trim()}
          onClick={() => void saveShot()}
        >
          <Plus size={13} /> {saving ? text("saving") : text("addShot")}
        </button>
      </div>

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
                  <strong>{shot.description}</strong>
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
