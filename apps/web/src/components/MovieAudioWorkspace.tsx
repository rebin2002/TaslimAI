"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, AudioLines, CheckCircle2, Clock3, Download, Music4, RefreshCw, ShieldAlert, Subtitles, X } from "lucide-react";
import {
  api,
  type MovieCaptionTrack,
  type MovieProject,
  type MovieSoundLibrary,
  type MovieSoundTrack,
  type MovieSoundtrack,
  type MovieSoundtrackCue,
} from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";
import { formatMovieNumber } from "@/lib/movieLocaleFormatting";
import type { Locale } from "@/lib/i18n";

/**
 * The sound stage. Everything rendered here is a persisted record: the project
 * sound library, per-shot sound tracks with real timing and gain, soundtrack
 * cues with versioned approvals and ducking intents, and caption tracks.
 * Generation is only offered when a real provider is configured, so nothing is
 * simulated when the seam is unavailable.
 */
export function MovieAudioWorkspace({ project }: { project: MovieProject }) {
  const { locale, t } = useLocale();
  const [library, setLibrary] = useState<MovieSoundLibrary | null>(null);
  const [tracks, setTracks] = useState<MovieSoundTrack[]>([]);
  const [soundtrack, setSoundtrack] = useState<MovieSoundtrack | null>(null);
  const [captionTracks, setCaptionTracks] = useState<MovieCaptionTrack[]>([]);
  const [loading, setLoading] = useState(true);
  const [busyTrackId, setBusyTrackId] = useState<string | null>(null);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const shots = useMemo(() => project.scenes.flatMap((scene) => scene.shots ?? []), [project]);

  const load = useCallback(async () => {
    const [libraryResult, soundtrackResult, captionResult, trackGroups] = await Promise.all([
      api.getMovieSoundLibrary(project.id).catch(() => null),
      api.getMovieSoundtrack(project.id).catch(() => null),
      api.getMovieCaptionTracks(project.id).catch(() => null),
      Promise.all(shots.slice(0, 40).map((shot) => api.getMovieShotSoundTracks(shot.id).catch(() => null))),
    ]);
    setLibrary(libraryResult);
    setSoundtrack(soundtrackResult);
    setCaptionTracks(captionResult ?? []);
    setTracks(
      trackGroups
        .filter((group): group is { targetId: string; targetType: string; tracks: MovieSoundTrack[] } => group !== null)
        .flatMap((group) => group.tracks)
        .sort((left, right) => left.startMilliseconds - right.startMilliseconds),
    );
    setLoading(false);
  }, [project.id, shots]);

  useEffect(() => {
    void Promise.resolve().then(() => load());
  }, [load]);

  async function review(track: MovieSoundTrack, approve: boolean) {
    setBusyTrackId(track.id);
    setError("");
    setMessage("");
    try {
      const updated = await api.reviewMovieSoundTrack(track.id, { approve });
      setTracks((current) => current.map((item) => (item.id === updated.id ? updated : item)));
      setMessage(approve ? "Sound cue approved." : "Sound cue rejected. The record keeps the decision.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The sound review could not be saved.");
    } finally {
      setBusyTrackId(null);
    }
  }

  const approved = tracks.filter((track) => track.status === "Approved").length;
  const cues = soundtrack?.cues ?? [];
  const approvedCues = cues.filter((cue) => cue.approvalState === "Approved").length;

  return (
    <div className="movie-audio-workspace" data-testid="movie-audio-workspace">
      <section className="movie-audio-command" aria-labelledby="movie-audio-title">
        <div>
          <span className="movie-workspace-kicker">{t("movieAudio.commandEyebrow")}</span>
          <h3 id="movie-audio-title">{t("movieAudio.commandTitle")}</h3>
          <p>{t("movieAudio.commandText")}</p>
        </div>
        <div className="movie-audio-command-mark">
          <AudioLines size={23} />
          <span>{t("movieAudio.persisted")}</span>
        </div>
      </section>

      <section className="movie-audio-summary" aria-label={t("movieAudio.summary")}>
        <AudioMetric locale={locale} label="Library" value={library?.references.length ?? 0} detail="approved references" />
        <AudioMetric locale={locale} label="Sound cues" value={tracks.length} detail={`${formatMovieNumber(approved, locale)} approved`} />
        <AudioMetric locale={locale} label="Score cues" value={cues.length} detail={`${formatMovieNumber(approvedCues, locale)} approved`} />
        <AudioMetric locale={locale} label="Caption tracks" value={captionTracks.length} detail={`${formatMovieNumber(captionTracks.filter((track) => track.isRtl).length, locale)} RTL`} />
      </section>

      {soundtrack && !soundtrack.mediaServiceAvailable && (
        <section className="movie-audio-boundary">
          <ShieldAlert size={16} />
          <div>
            <strong>Score generation is not connected</strong>
            <span>
              Cue, version, ducking and approval data are fully persisted, but no soundtrack media provider is
              configured for this deployment. Cues stay reviewable and can carry an approved asset.
            </span>
          </div>
        </section>
      )}

      {message && (
        <div className="movie-selects-notice is-success" role="status">
          <CheckCircle2 size={15} />
          <span>{message}</span>
        </div>
      )}
      {error && (
        <div className="movie-selects-notice is-error" role="alert">
          <AlertTriangle size={15} />
          <span>{error}</span>
          <button type="button" onClick={() => setError("")} aria-label="Dismiss error">
            <X size={13} />
          </button>
        </div>
      )}

      <section className="movie-audio-section" aria-labelledby="movie-audio-tracks-title">
        <div className="movie-audio-section-heading">
          <div>
            <span className="movie-workspace-kicker">{t("movieAudio.tracks")}</span>
            <h3 id="movie-audio-tracks-title">{t("movieAudio.tracksTitle")}</h3>
            <p>{t("movieAudio.tracksText")}</p>
          </div>
          <button type="button" className="movie-workspace-button is-quiet" onClick={() => void load()}>
            <RefreshCw size={13} /> {t("movieAudio.refresh")}
          </button>
        </div>
        {loading ? (
          <div className="movie-audio-empty">
            <Clock3 size={18} />
            <span>{t("movieAudio.loading")}</span>
          </div>
        ) : tracks.length === 0 ? (
          <div className="movie-audio-empty">
            <AudioLines size={20} />
            <strong>No sound tracks yet</strong>
            <p>
              Sound tracks appear after a real cue is created for a scene or shot. Nothing is generated or simulated
              here.
            </p>
          </div>
        ) : (
          <div className="movie-audio-tracks">
            {tracks.map((track) => (
              <article key={track.id} className="movie-audio-track">
                <header>
                  <div>
                    <span className="movie-audio-track-kicker">
                      {track.kind} · {track.layer}
                    </span>
                    <h4>{track.name}</h4>
                    <p>{track.description}</p>
                  </div>
                  <span className={`movie-audio-status is-${track.status.toLowerCase()}`}>{track.status}</span>
                </header>
                <dl className="movie-audio-facts">
                  <div>
                    <dt>Window</dt>
                    <dd>
                      {formatMovieNumber(track.startMilliseconds, locale)}–{formatMovieNumber(track.endMilliseconds, locale)} ms
                    </dd>
                  </div>
                  <div>
                    <dt>Fades</dt>
                    <dd>
                      {formatMovieNumber(track.fadeInMilliseconds, locale)} / {formatMovieNumber(track.fadeOutMilliseconds, locale)} ms
                    </dd>
                  </div>
                  <div>
                    <dt>Gain</dt>
                    <dd>{formatMovieNumber(track.gainDb, locale)} dB</dd>
                  </div>
                  <div>
                    <dt>Source</dt>
                    <dd>{track.sourceKind}</dd>
                  </div>
                </dl>
                <footer>
                  {track.assetId && <audio src={`/api/assets/${track.assetId}/download?inline=true`} controls preload="metadata" aria-label={`${track.name} preview`} />}
                  <div className="movie-audio-track-actions">
                    {track.status !== "Approved" && (
                      <button
                        type="button"
                        className="movie-workspace-button is-secondary"
                        onClick={() => void review(track, true)}
                        disabled={busyTrackId === track.id}
                      >
                        Approve cue
                      </button>
                    )}
                    {track.status !== "Rejected" && (
                      <button
                        type="button"
                        className="movie-text-action is-danger"
                        onClick={() => void review(track, false)}
                        disabled={busyTrackId === track.id}
                      >
                        <X size={12} /> Reject
                      </button>
                    )}
                  </div>
                </footer>
              </article>
            ))}
          </div>
        )}
      </section>

      <section className="movie-audio-section" aria-labelledby="movie-audio-score-title">
        <div className="movie-audio-section-heading">
          <div>
            <span className="movie-workspace-kicker">Score · soundtrack cues</span>
            <h3 id="movie-audio-score-title">Where the music belongs.</h3>
            <p>Cues carry a mood, intensity, timeline placement and explicit ducking intents.</p>
          </div>
          <Music4 size={18} />
        </div>
        {cues.length === 0 ? (
          <div className="movie-audio-empty">
            <Music4 size={20} />
            <strong>No score cues yet</strong>
            <p>A cue appears here after it is created against a real scene in this project.</p>
          </div>
        ) : (
          <div className="movie-audio-cues">
            {cues.map((cue) => (
              <ScoreCue key={cue.id} cue={cue} />
            ))}
          </div>
        )}
      </section>

      <section className="movie-audio-section" aria-labelledby="movie-audio-captions-title">
        <div className="movie-audio-section-heading">
          <div>
            <span className="movie-workspace-kicker">Captions and subtitles</span>
            <h3 id="movie-audio-captions-title">Accessible delivery.</h3>
            <p>Tracks keep their language and direction so RTL delivery stays correct.</p>
          </div>
          <Subtitles size={18} />
        </div>
        {captionTracks.length === 0 ? (
          <div className="movie-audio-empty">
            <Subtitles size={20} />
            <strong>No caption tracks yet</strong>
            <p>Caption tracks appear after they are authored or imported for this project.</p>
          </div>
        ) : (
          <div className="movie-audio-captions">
            {captionTracks.map((track) => (
              <article key={track.id} className="movie-audio-caption">
                <header>
                  <div>
                    <span className="movie-audio-track-kicker">
                      {track.trackType} · {track.language}
                      {track.isRtl ? " · RTL" : ""}
                    </span>
                    <h4>{track.name}</h4>
                  </div>
                  <span className={`movie-audio-status is-${track.status.toLowerCase()}`}>{track.status}</span>
                </header>
                <p>
                  {formatMovieNumber(track.cues.length, locale)} cue(s)
                  {track.isDefault ? " · default track" : ""}
                  {track.sourceFormat ? ` · imported ${track.sourceFormat.toUpperCase()}` : ""}
                </p>
                {track.cues.length > 0 && (
                  <ol className="movie-audio-caption-cues" dir={track.isRtl ? "rtl" : "ltr"}>
                    {track.cues.slice(0, 6).map((cue) => (
                      <li key={cue.id}>
                        <time>
                          {cue.startTimecode} → {cue.endTimecode}
                        </time>
                        <span>{cue.text}</span>
                      </li>
                    ))}
                  </ol>
                )}
                <footer>
                  <a className="movie-text-action" href={api.movieCaptionExportUrl(track.id, "srt")} download>
                    <Download size={12} /> SRT
                  </a>
                  <a className="movie-text-action" href={api.movieCaptionExportUrl(track.id, "vtt")} download>
                    <Download size={12} /> VTT
                  </a>
                </footer>
              </article>
            ))}
          </div>
        )}
      </section>

      <div className="movie-audio-footnote">
        <Clock3 size={14} />
        <span>
          Audio records are workspace-scoped. Previewing a cue uses the private asset download path; no provider URL is
          exposed.
        </span>
      </div>
    </div>
  );
}

function ScoreCue({ cue }: { cue: MovieSoundtrackCue }) {
  const { locale } = useLocale();
  const approvedVersion = cue.versions.find((version) => version.id === cue.approvedVersionId) ?? null;
  return (
    <article className="movie-audio-cue">
      <header>
        <div>
          <span className="movie-audio-track-kicker">
            Cue {formatMovieNumber(cue.sequence, locale, { minimumIntegerDigits: 2 })} · {cue.mood} · intensity {formatMovieNumber(cue.intensity, locale)}
          </span>
          <h4>{cue.title}</h4>
          <p>{cue.narrativeIntent ?? "No narrative intent recorded."}</p>
        </div>
        <span className={`movie-audio-status is-${cue.approvalState.toLowerCase()}`}>{cue.approvalState}</span>
      </header>
      <dl className="movie-audio-facts">
        <div>
          <dt>Timeline</dt>
          <dd>{formatMovieNumber(cue.timelineStartSeconds, locale)}s</dd>
        </div>
        <div>
          <dt>Duration</dt>
          <dd>{formatMovieNumber(cue.durationSeconds, locale)}s</dd>
        </div>
        <div>
          <dt>Versions</dt>
          <dd>{formatMovieNumber(cue.versions.length, locale)}</dd>
        </div>
        <div>
          <dt>Ducking</dt>
          <dd>{formatMovieNumber(cue.duckingIntents.length, locale)} intent(s)</dd>
        </div>
      </dl>
      {cue.duckingIntents.length > 0 && (
        <ul className="movie-audio-ducking">
          {cue.duckingIntents.map((intent) => (
            <li key={intent.id}>
              Duck {intent.targetLane} by {intent.duckDecibels} dB · {intent.startOffsetSeconds}s–{intent.endOffsetSeconds}s
            </li>
          ))}
        </ul>
      )}
      {approvedVersion && (
        <div className="movie-audio-approved">
          <CheckCircle2 size={13} />
          <span>
            Approved version {approvedVersion.versionNumber} · {approvedVersion.label}
            {approvedVersion.audioAssetProvenance ? ` · ${approvedVersion.audioAssetProvenance.mimeType}` : ""}
          </span>
        </div>
      )}
      {!approvedVersion && cue.versions.length > 0 && (
        <div className="movie-audio-approved is-pending">
          <Clock3 size={13} />
          <span>No version has been approved yet. The cue stays review-only.</span>
        </div>
      )}
    </article>
  );
}

function AudioMetric({ locale, label, value, detail }: { locale: Locale; label: string; value: number; detail: string }) {
  return (
    <div className="movie-audio-metric">
      <span>{label}</span>
      <strong>{formatMovieNumber(value, locale)}</strong>
      <small>{detail}</small>
    </div>
  );
}
