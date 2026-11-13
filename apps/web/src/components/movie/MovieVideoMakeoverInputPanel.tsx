/**
 * Standalone AI Video Makeover input panel (issue #698, input-UI slice).
 *
 * Isolation guarantees, mirrored from the provider-neutral draft/admission
 * helper in `@/lib/movieVideoMakeoverDraftState`:
 *
 * 1. NOT WIRED TO ANY PROVIDER OR BACKEND. This component imports no API
 *    client, issues no fetch, creates no job, reserves no credit, and renders
 *    no enabled estimate/launch control. Both action buttons are rendered
 *    permanently disabled with `title="Not wired yet"`; the admission helper
 *    keeps `uiEnabled`/`billable`/`launchPermitted` at the literal `false`, and
 *    this panel displays that rather than implying otherwise.
 * 2. HONEST UNSUPPORTED STATES. Outfit/wardrobe change (#698 Phase B) is
 *    selectable only to demonstrate the blocked path: it is labelled
 *    "Future feature" and its admission carries
 *    `wardrobe_unsupported_future_feature` from the shared helper. Capability
 *    chips default to `unknown` evidence, which can never read as support.
 * 3. NO OWNED-FILE DEPENDENCIES. MovieStudioView (#762) is NOT imported and
 *    this panel is NOT referenced from it; integration is a separate, owned
 *    change. No i18n keys are claimed — labels are panel-local constants so
 *    `@/lib/i18n.ts` (#762 ownership) is untouched.
 * 4. FULLY CONTROLLED. The panel holds no hidden state: every rendered value
 *    comes from props, so its behaviour is testable with `renderToStaticMarkup`
 *    like the rest of this repo's component tests (no DOM shim needed).
 */

import {
  admitMovieVideoMakeoverDraft,
  createMovieVideoMakeoverDraft,
  type MovieVideoMakeoverAdmission,
} from "@/lib/movieVideoMakeoverDraftState";
import {
  MOVIE_VIDEO_MAKEOVER_UI_EVIDENCE_CHOICES,
  MOVIE_VIDEO_MAKEOVER_UI_FEATURE_LABELS,
  MOVIE_VIDEO_MAKEOVER_UI_MODE_CHOICES,
  MOVIE_VIDEO_MAKEOVER_UI_PRESET_IDS,
  movieVideoMakeoverSelectedRequiredFeatures,
  movieVideoMakeoverUiStatus,
  buildMovieVideoMakeoverDraftInput,
  type MovieVideoMakeoverCapabilitySelection,
  type MovieVideoMakeoverSelection,
} from "./movieVideoMakeoverInputState";

export type MovieVideoMakeoverInputPanelProps = {
  readonly selection: MovieVideoMakeoverSelection;
  readonly onChange: (nextSelection: MovieVideoMakeoverSelection) => void;
};

export function MovieVideoMakeoverInputPanel({ selection, onChange }: MovieVideoMakeoverInputPanelProps) {
  const admission: MovieVideoMakeoverAdmission = admitMovieVideoMakeoverDraft(
    createMovieVideoMakeoverDraft(buildMovieVideoMakeoverDraftInput(selection)),
  );
  const status = movieVideoMakeoverUiStatus(admission);
  const requiredFeatures = movieVideoMakeoverSelectedRequiredFeatures(selection);
  const blockedField = (field: string): boolean => status.blockedFields.has(field);

  const setField = <K extends keyof MovieVideoMakeoverSelection>(key: K, value: MovieVideoMakeoverSelection[K]): void => {
    onChange({ ...selection, [key]: value });
  };

  const setCapabilityRow = (feature: string, patch: Partial<MovieVideoMakeoverCapabilitySelection>): void => {
    const existing = selection.capabilities.find((row) => row.feature === feature);
    const base: MovieVideoMakeoverCapabilitySelection = existing ?? {
      feature: feature as MovieVideoMakeoverCapabilitySelection["feature"],
      selected: false,
      evidenceLabel: "unknown",
      sourceUrl: null,
      capturedAtUtc: null,
    };
    const next = { ...base, ...patch };
    const others = selection.capabilities.filter((row) => row.feature !== feature);
    onChange({ ...selection, capabilities: [...others, next] });
  };

  return (
    <section className="movie-makeover-input-panel" aria-label="AI Video Makeover inputs">
      <header className="movie-makeover-input-header">
        <h3>AI Video Makeover</h3>
        <p className="movie-makeover-wiring-notice" role="note">
          {"Draft input only. No provider is wired: estimate and start controls stay disabled and no credit is reserved."}
        </p>
      </header>

      <fieldset className="movie-makeover-modes" aria-label="Makeover mode">
        <legend>Mode</legend>
        {MOVIE_VIDEO_MAKEOVER_UI_MODE_CHOICES.map((choice) => (
          <label key={choice.value} className={choice.supported ? "movie-makeover-mode" : "movie-makeover-mode movie-makeover-mode-future"}>
            <input
              type="radio"
              name="movie-makeover-mode"
              value={choice.value}
              checked={selection.mode === choice.value}
              onChange={() => setField("mode", choice.value)}
            />
            <span>{choice.label}</span>
            {!choice.supported && (
              <span className="movie-makeover-future-badge" aria-label="Support status">
                {choice.availabilityLabel}
              </span>
            )}
          </label>
        ))}
        {blockedField("mode") && (
          <p className="movie-makeover-field-error" role="alert">
            {admission.blockedReasons.find((reason) => reason.field === "mode")?.message}
          </p>
        )}
      </fieldset>

      <div className="movie-makeover-source" aria-label="Source video">
        <span className="movie-makeover-source-label">Source video</span>
        {selection.sourceAssetId === null ? (
          <button
            type="button"
            onClick={() => setField("sourceAssetId", "workspace-clip-001")}
          >
            Choose an uploaded clip
          </button>
        ) : (
          <span className="movie-makeover-source-selected">
            Selected clip: {selection.sourceAssetId}
            <button type="button" onClick={() => setField("sourceAssetId", null)}>
              Remove
            </button>
          </span>
        )}
        {blockedField("source.assetId") && (
          <p className="movie-makeover-field-error" role="alert">
            Attach the source video to makeover.
          </p>
        )}
      </div>

      <fieldset className="movie-makeover-presets" aria-label="Preset">
        <legend>Preset</legend>
        {MOVIE_VIDEO_MAKEOVER_UI_PRESET_IDS.map((presetId) => (
          <label key={presetId} className="movie-makeover-preset">
            <input
              type="radio"
              name="movie-makeover-preset"
              value={presetId}
              checked={selection.presetId === presetId}
              onChange={() => setField("presetId", presetId)}
            />
            <span>{presetId}</span>
          </label>
        ))}
        {blockedField("presetId") && (
          <p className="movie-makeover-field-error" role="alert">
            {admission.blockedReasons.find((reason) => reason.field === "presetId")?.message}
          </p>
        )}
      </fieldset>

      <label className="movie-makeover-prompt">
        Optional prompt
        <textarea
          value={selection.prompt}
          onChange={(event) => setField("prompt", event.target.value)}
          rows={2}
          maxLength={2000}
        />
      </label>

      <fieldset className="movie-makeover-capabilities" aria-label="Capability evidence">
        <legend>Verified capabilities</legend>
        <p className="movie-makeover-capability-note">
          Required for this mode:{" "}
          {requiredFeatures.length === 0
            ? "none until a supported mode is selected"
            : requiredFeatures.map((feature) => MOVIE_VIDEO_MAKEOVER_UI_FEATURE_LABELS[feature]).join(", ")}
          . A feature counts only when documented by the provider and confirmed by a tested capability.
        </p>
        {requiredFeatures.map((feature) => {
          const row = selection.capabilities.find((capability) => capability.feature === feature);
          const selected = row?.selected === true;
          const evidence = row?.evidenceLabel ?? "unknown";
          return (
            <div key={feature} className="movie-makeover-capability-row">
              <label>
                <input
                  type="checkbox"
                  checked={selected}
                  onChange={(event) => setCapabilityRow(feature, { selected: event.target.checked })}
                />
                <span>{MOVIE_VIDEO_MAKEOVER_UI_FEATURE_LABELS[feature]}</span>
              </label>
              <label className="movie-makeover-capability-evidence">
                Evidence
                <select
                  value={evidence}
                  onChange={(event) =>
                    setCapabilityRow(feature, {
                      evidenceLabel: event.target.value as MovieVideoMakeoverCapabilitySelection["evidenceLabel"],
                    })
                  }
                >
                  {MOVIE_VIDEO_MAKEOVER_UI_EVIDENCE_CHOICES.map((choice) => (
                    <option key={choice.value} value={choice.value}>
                      {choice.label}
                    </option>
                  ))}
                </select>
              </label>
            </div>
          );
        })}
      </fieldset>

      <fieldset className="movie-makeover-preservation" aria-label="Preservation and consent">
        <legend>Preservation and consent</legend>
        <label>
          <input
            type="checkbox"
            checked={selection.preserveSubjectIdentity}
            onChange={(event) => setField("preserveSubjectIdentity", event.target.checked)}
          />
          <span>Preserve subject identity (never swap faces)</span>
        </label>
        <label>
          <input
            type="checkbox"
            checked={selection.preserveOriginalAudio}
            onChange={(event) => setField("preserveOriginalAudio", event.target.checked)}
          />
          <span>Preserve original audio</span>
        </label>
        <label>
          <input
            type="checkbox"
            checked={selection.creatorApproved}
            onChange={(event) => setField("creatorApproved", event.target.checked)}
          />
          <span>Creator explicitly approves this makeover draft</span>
        </label>
      </fieldset>

      <footer className="movie-makeover-status" aria-live="polite">
        <p className="movie-makeover-status-headline">{status.headline}</p>
        <ul className="movie-makeover-blocked-list">
          {admission.blockedReasons.map((reason) => (
            <li key={reason.code + ":" + reason.field} data-block-code={reason.code}>
              {reason.field}: {reason.message}
            </li>
          ))}
        </ul>
        <div className="movie-makeover-actions">
          <button type="button" disabled title="Not wired yet">
            {status.estimateButtonEnabledWording}
          </button>
          <button type="button" disabled title="Not wired yet">
            Start makeover
          </button>
        </div>
        <p className="movie-makeover-safety-line">
          {"launchPermitted: " + String(admission.launchPermitted) + " \u00B7 billable: " + String(admission.billable)
            + " \u00B7 providerJobCreated: " + String(admission.providerJobCreated)
            + " \u00B7 uiEnabled: " + String(admission.uiEnabled)}
        </p>
      </footer>
    </section>
  );
}
