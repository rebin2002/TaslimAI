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
  intentLabel,
  parseCinematographyJson,
  selectionFromPreset,
  type CinematographyControlKey,
} from "@/lib/movieCinematography";

const controlLabels: Record<CinematographyControlKey, string> = {
  shotSize: "Shot size",
  focalLength: "Focal length intent",
  lensIntent: "Lens type / intent",
  apertureDepthOfField: "Aperture / depth",
  cameraAngle: "Camera angle",
  cameraMovement: "Camera movement",
  frameRateIntent: "Frame rate",
  lighting: "Lighting",
  paletteLook: "Palette / look",
  compositionNotes: "Composition",
};

const capabilityLegend = [
  "Native",
  "Translated",
  "Simulated/Post",
  "Unsupported",
] as const;

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
            <strong id={`shot-designer-${scene.id}`}>Shot Designer</strong>
            <small>Shape the camera without needing to know lenses.</small>
          </div>
        </div>
        <span className="movie-shot-saved-label">
          <span className="movie-live-dot" /> Saved to shot plan
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
          <span>Guide baseline</span>
          <strong>
            {guideBaseline
              ? `${intentLabel(guideBaseline.intent)} · ${guideBaseline.name}`
              : guideBible?.intent
                ? intentLabel(guideBible.intent)
                : "No cinematography direction set"}
          </strong>
          <p>
            {guideBible?.notes ||
              guideBaseline?.summary ||
              "This shot can establish its own direction. A shot override never rewrites the Movie Guide."}
          </p>
        </div>
        <span className="movie-guide-relationship-status">
          {guideIsLocked
            ? `Locked · rev ${guide.lockedRevisionNumber}`
            : "Project direction"}
        </span>
      </div>

      <div
        className="movie-shot-mode-switch"
        role="tablist"
        aria-label="Shot Designer mode"
      >
        <button
          type="button"
          role="tab"
          aria-selected={mode === "simple"}
          className={mode === "simple" ? "is-active" : ""}
          onClick={() => setMode("simple")}
        >
          <Sparkles size={13} /> Simple
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={mode === "advanced"}
          className={mode === "advanced" ? "is-active" : ""}
          onClick={() => setMode("advanced")}
        >
          <ChevronDown size={13} /> Advanced
        </button>
      </div>

      <div className="movie-shot-intent-heading">
        <div>
          <span>Creative intent</span>
          <small>
            Choose a feeling. Taslim fills the production direction.
          </small>
        </div>
        <span className="movie-shot-override-note">Per-shot override</span>
      </div>
      <div
        className="movie-shot-intents"
        role="radiogroup"
        aria-label="Creative intent"
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
              <strong>{intentLabel(intent)}</strong>
              <small>{preset?.summary ?? "Production direction"}</small>
            </button>
          );
        })}
      </div>

      <label className="movie-shot-field movie-shot-description">
        <span>Shot description</span>
        <textarea
          value={draft.description}
          onChange={(event) =>
            setDraft((current) => ({
              ...current,
              description: event.target.value,
            }))
          }
          placeholder="What happens in this shot?"
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
              {selectedPreset?.name ?? intentLabel(draft.cinematography.intent)}{" "}
              direction ready
            </strong>
            <p>
              {selectedPreset?.summary ??
                "Select an intent to populate a sensible cinematography direction."}
            </p>
            <small>
              Capability truth stays explicit: Native · Translated ·
              Simulated/Post · Unsupported.
            </small>
          </div>
          <button type="button" onClick={() => setMode("advanced")}>
            Tune details
          </button>
        </div>
      ) : (
        <div className="movie-shot-advanced-panel">
          <div className="movie-shot-advanced-heading">
            <div>
              <span>Advanced controls</span>
              <small>
                Every value is a production intent, not a promise of native
                provider support.
              </small>
            </div>
            <label className="movie-shot-preset-field">
              <span>Preset</span>
              <select
                aria-label="Cinematography preset"
                value={draft.cinematography.presetId ?? "custom"}
                onChange={(event) => choosePreset(event.target.value)}
              >
                <option value="custom">Custom direction</option>
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
                <span>{controlLabels[key]}</span>
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
          <CapabilityTruth selection={draft.cinematography} />
        </div>
      )}

      <div className="movie-shot-designer-footer">
        <label className="movie-shot-duration">
          <span>Shot duration</span>
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
          <em>sec</em>
        </label>
        <span className="movie-shot-save-note">
          {guideIsLocked
            ? "Guide stays locked; this saves as a shot-level override."
            : "The Movie Guide remains unchanged; this saves a shot-level override."}
        </span>
        <button
          type="button"
          className="movie-workspace-button is-primary"
          disabled={saving || !draft.description.trim()}
          onClick={() => void saveShot()}
        >
          <Plus size={13} /> {saving ? "Saving…" : "Add shot"}
        </button>
      </div>

      {scene.shots.length > 0 && (
        <div className="movie-shot-list" aria-label="Saved shots">
          <div className="movie-shot-list-heading">
            <span>Saved shots</span>
            <small>{scene.shots.length}</small>
          </div>
          {scene.shots.map((shot) => {
            const selection = parseCinematographyJson(shot.cinematographyJson);
            return (
              <div className="movie-shot-list-item" key={shot.id}>
                <span>{String(shot.sequence).padStart(2, "0")}</span>
                <div>
                  <strong>{shot.description}</strong>
                  <small>
                    {selection
                      ? `${intentLabel(selection.intent)} override${selection.presetId ? ` · ${presets.find((preset) => preset.id === selection.presetId)?.name ?? "custom"}` : ""}`
                      : "No cinematography direction"}
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
  selection,
}: {
  selection: CinematographyIntentSelection;
}) {
  const references = selection.capabilityReferences ?? [];
  return (
    <div className="movie-capability-truth">
      <div className="movie-capability-heading">
        <div>
          <span>Capability truth</span>
          <small>
            Resolution stays explicit and provider/model names stay out of the
            creative workflow.
          </small>
        </div>
        <div className="movie-capability-legend">
          {capabilityLegend.map((item) => (
            <span key={item}>{item}</span>
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
                {controlLabels[reference.field as CinematographyControlKey] ??
                  reference.field}
              </strong>
              <span>{reference.classification}</span>
              <small>
                {reference.rationale ||
                  "Classification is retained for integration-time resolution."}
              </small>
            </div>
          ))}
        </div>
      ) : (
        <p className="movie-capability-empty">
          This direction has no field-level classification yet. It remains
          production intent and will not be presented as native support.
        </p>
      )}
    </div>
  );
}

export type { ShotDesignerDraft };
