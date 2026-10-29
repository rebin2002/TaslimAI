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
import {
  formatMovieAudioNumber,
  movieAudioStatusLabel,
  movieAudioText,
  type MovieAudioKey,
} from "@/lib/movieAudioI18n";

/**
 * The sound stage. Everything rendered here is a persisted record: the project
 * sound library, per-shot sound tracks with real timing and gain, soundtrack
 * cues with versioned approvals and ducking intents, and caption tracks.
 * Generation is only offered when a real provider is configured, so nothing is
 * simulated when the seam is unavailable.
 */
export function MovieAudioWorkspace({ project }: { project: MovieProject }) {
  const { locale, t } = useLocale();
  const text = (key: MovieAudioKey, variables?: Record<string, string | number>) =>
    movieAudioText(locale, key, variables);
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
      setMessage(text(approve ? "reviewApproved" : "reviewRejected"));
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : text("reviewSaveError"));
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
        <AudioMetric locale={locale} label={text("summaryLibrary")} value={library?.references.length ?? 0} detail={text("summaryApprovedReferences")} />
        <AudioMetric locale={locale} label={text("summarySoundCues")} value={tracks.length} detail={text("summaryApprovedCues", { count: formatMovieAudioNumber(locale, approved) })} />
        <AudioMetric locale={locale} label={text("summaryScoreCues")} value={cues.length} detail={text("summaryApprovedCues", { count: formatMovieAudioNumber(locale, approvedCues) })} />
        <AudioMetric locale={locale} label={text("summaryCaptionTracks")} value={captionTracks.length} detail={text("summaryRtlTracks", { count: formatMovieAudioNumber(locale, captionTracks.filter((track) => track.isRtl).length) })} />
      </section>

      {soundtrack && !soundtrack.mediaServiceAvailable && (
        <section className="movie-audio-boundary">
          <ShieldAlert size={16} />
          <div>
            <strong>{text("providerUnavailableTitle")}</strong>
            <span>{text("providerUnavailableText")}</span>
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
          <button type="button" onClick={() => setError("")} aria-label={text("dismissError")}>
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
            <strong>{text("noSoundTracks")}</strong>
            <p>{text("noSoundTracksText")}</p>
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
                  <span className={`movie-audio-status is-${track.status.toLowerCase()}`}>{movieAudioStatusLabel(locale, track.status)}</span>
                </header>
                <dl className="movie-audio-facts">
                  <div>
                    <dt>{text("window")}</dt>
                    <dd>
                      {formatMovieAudioNumber(locale, track.startMilliseconds)}–{formatMovieAudioNumber(locale, track.endMilliseconds)} ms
                    </dd>
                  </div>
                  <div>
                    <dt>{text("fades")}</dt>
                    <dd>
                      {formatMovieAudioNumber(locale, track.fadeInMilliseconds)} / {formatMovieAudioNumber(locale, track.fadeOutMilliseconds)} ms
                    </dd>
                  </div>
                  <div>
                    <dt>{text("gain")}</dt>
                    <dd>{formatMovieAudioNumber(locale, track.gainDb)} dB</dd>
                  </div>
                  <div>
                    <dt>{text("source")}</dt>
                    <dd>{track.sourceKind}</dd>
                  </div>
                </dl>
                <footer>
                  {track.assetId && <audio src={`/api/assets/${track.assetId}/download?inline=true`} controls preload="metadata" aria-label={text("previewAudio", { name: track.name })} />}
                  <div className="movie-audio-track-actions">
                    {track.status !== "Approved" && (
                      <button
                        type="button"
                        className="movie-workspace-button is-secondary"
                        onClick={() => void review(track, true)}
                        disabled={busyTrackId === track.id}
                      >
                        {text("approveCue")}
                      </button>
                    )}
                    {track.status !== "Rejected" && (
                      <button
                        type="button"
                        className="movie-text-action is-danger"
                        onClick={() => void review(track, false)}
                        disabled={busyTrackId === track.id}
                      >
                        <X size={12} /> {text("rejectCue")}
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
            <span className="movie-workspace-kicker">{text("scoreEyebrow")}</span>
            <h3 id="movie-audio-score-title">{text("scoreTitle")}</h3>
            <p>{text("scoreText")}</p>
          </div>
          <Music4 size={18} />
        </div>
        {cues.length === 0 ? (
          <div className="movie-audio-empty">
            <Music4 size={20} />
            <strong>{text("noScoreCues")}</strong>
            <p>{text("noScoreCuesText")}</p>
          </div>
        ) : (
          <div className="movie-audio-cues">
            {cues.map((cue) => (
              <ScoreCue key={cue.id} cue={cue} locale={locale} text={text} />
            ))}
          </div>
        )}
      </section>

      <section className="movie-audio-section" aria-labelledby="movie-audio-captions-title">
        <div className="movie-audio-section-heading">
          <div>
            <span className="movie-workspace-kicker">{text("captionsEyebrow")}</span>
            <h3 id="movie-audio-captions-title">{text("captionsTitle")}</h3>
            <p>{text("captionsText")}</p>
          </div>
          <Subtitles size={18} />
        </div>
        {captionTracks.length === 0 ? (
          <div className="movie-audio-empty">
            <Subtitles size={20} />
            <strong>{text("noCaptionTracks")}</strong>
            <p>{text("noCaptionTracksText")}</p>
          </div>
        ) : (
          <div className="movie-audio-captions">
            {captionTracks.map((track) => (
              <article key={track.id} className="movie-audio-caption">
                <header>
                  <div>
                    <span className="movie-audio-track-kicker">
                      {track.trackType} · {track.language}
                      {track.isRtl ? ` · ${text("rtl")}` : ""}
                    </span>
                    <h4>{track.name}</h4>
                  </div>
                  <span className={`movie-audio-status is-${track.status.toLowerCase()}`}>{movieAudioStatusLabel(locale, track.status)}</span>
                </header>
                <p>
                  {text("cueCount", { count: formatMovieAudioNumber(locale, track.cues.length) })}
                  {track.isDefault ? ` · ${text("defaultTrack")}` : ""}
                  {track.sourceFormat ? ` · ${text("imported", { format: track.sourceFormat.toUpperCase() })}` : ""}
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
        <span>{text("footnote")}</span>
      </div>
    </div>
  );
}

function ScoreCue({ cue, locale, text }: { cue: MovieSoundtrackCue; locale: Parameters<typeof movieAudioText>[0]; text: (key: MovieAudioKey, variables?: Record<string, string | number>) => string }) {
  const approvedVersion = cue.versions.find((version) => version.id === cue.approvedVersionId) ?? null;
  return (
    <article className="movie-audio-cue">
      <header>
        <div>
          <span className="movie-audio-track-kicker">
            {text("cue", { number: formatMovieAudioNumber(locale, cue.sequence), mood: cue.mood, intensity: formatMovieAudioNumber(locale, cue.intensity) })}
          </span>
          <h4>{cue.title}</h4>
          <p>{cue.narrativeIntent ?? text("noNarrativeIntent")}</p>
        </div>
        <span className={`movie-audio-status is-${cue.approvalState.toLowerCase()}`}>{movieAudioStatusLabel(locale, cue.approvalState)}</span>
      </header>
      <dl className="movie-audio-facts">
        <div>
          <dt>{text("timeline")}</dt>
          <dd>{formatMovieAudioNumber(locale, cue.timelineStartSeconds)}s</dd>
        </div>
        <div>
          <dt>{text("duration")}</dt>
          <dd>{formatMovieAudioNumber(locale, cue.durationSeconds)}s</dd>
        </div>
        <div>
          <dt>{text("versions")}</dt>
          <dd>{formatMovieAudioNumber(locale, cue.versions.length)}</dd>
        </div>
        <div>
          <dt>{text("ducking")}</dt>
          <dd>{formatMovieAudioNumber(locale, cue.duckingIntents.length)} {text("duckingIntentCount")}</dd>
        </div>
      </dl>
      {cue.duckingIntents.length > 0 && (
        <ul className="movie-audio-ducking">
          {cue.duckingIntents.map((intent) => (
            <li key={intent.id}>
              {text("duckingIntent", {
                target: intent.targetLane,
                decibels: formatMovieAudioNumber(locale, intent.duckDecibels),
                start: formatMovieAudioNumber(locale, intent.startOffsetSeconds),
                end: formatMovieAudioNumber(locale, intent.endOffsetSeconds),
              })}
            </li>
          ))}
        </ul>
      )}
      {approvedVersion && (
        <div className="movie-audio-approved">
          <CheckCircle2 size={13} />
          <span>
            {text("approvedVersion", {
              version: formatMovieAudioNumber(locale, approvedVersion.versionNumber),
              label: approvedVersion.label,
            })}
            {approvedVersion.audioAssetProvenance ? ` · ${approvedVersion.audioAssetProvenance.mimeType}` : ""}
          </span>
        </div>
      )}
      {!approvedVersion && cue.versions.length > 0 && (
        <div className="movie-audio-approved is-pending">
          <Clock3 size={13} />
          <span>{text("noApprovedVersion")}</span>
        </div>
      )}
    </article>
  );
}

function AudioMetric({ locale, label, value, detail }: { locale: Parameters<typeof movieAudioText>[0]; label: string; value: number; detail: string }) {
  return (
    <div className="movie-audio-metric">
      <span>{label}</span>
      <strong>{formatMovieAudioNumber(locale, value)}</strong>
      <small>{detail}</small>
    </div>
  );
}
