"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import {
  AlertTriangle,
  CheckCircle2,
  Clock3,
  Download,
  Film,
  RefreshCw,
  ShieldCheck,
  X,
} from "lucide-react";
import { api, type MovieFinalAssembly, type MovieProject } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";
import {
  formatMovieDeliveryDate,
  formatMovieDeliveryNumber,
  movieDeliveryCaptionModeLabel,
  movieDeliveryStatusLabel,
  movieDeliveryText,
} from "@/lib/movieDeliveryI18n";

const profiles = [
  { id: "hd-1080p", label: "HD 1080p", detailKey: "fastDelivery" as const },
  { id: "uhd-4k", label: "UHD 4K", detailKey: "masterDelivery" as const },
];

const terminalStatuses = new Set(["Ready", "Failed", "Cancelled"]);

/**
 * The terminal step of the customer workflow: turn the approved, selected takes
 * into one durable private master and hand the customer a real download. Every
 * value shown here comes from the persisted final assembly record.
 */
export function MovieExportsWorkspace({ project }: { project: MovieProject }) {
  const { locale, t } = useLocale();
  const text = (
    key: Parameters<typeof movieDeliveryText>[1],
    variables?: Record<string, string | number>,
  ) => movieDeliveryText(locale, key, variables);
  const [assemblies, setAssemblies] = useState<MovieFinalAssembly[]>([]);
  const [profile, setProfile] = useState("hd-1080p");
  const [includeApprovedSoundtrackCues, setIncludeApprovedSoundtrackCues] =
    useState(false);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const shots = useMemo(
    () => project.scenes.flatMap((scene) => scene.shots ?? []),
    [project],
  );
  const takes = useMemo(
    () => shots.flatMap((shot) => shot.takes ?? []),
    [shots],
  );
  const finalized = useMemo(
    () => takes.filter((take) => take.finalizedAt),
    [takes],
  );
  const selected = useMemo(
    () => takes.filter((take) => take.selectedAt),
    [takes],
  );
  const readyToAssemble = finalized.length > 0 || selected.length > 0;
  const latest = assemblies[0] ?? null;

  const load = useCallback(async () => {
    const known = project.assemblies ?? [];
    if (known.length === 0) {
      setAssemblies([]);
      setLoading(false);
      return;
    }
    const detailed = await Promise.all(
      known.map((item) => api.getMovieFinalAssembly(item.id).catch(() => null)),
    );
    setAssemblies(
      detailed
        .filter((item): item is MovieFinalAssembly => item !== null)
        .sort((left, right) => right.createdAt.localeCompare(left.createdAt)),
    );
    setLoading(false);
  }, [project.assemblies]);

  useEffect(() => {
    void Promise.resolve().then(() => load());
  }, [load]);

  // Poll only while a real assembly is still being produced.
  useEffect(() => {
    if (!latest || terminalStatuses.has(latest.status)) return;
    const timer = setInterval(() => {
      void api
        .getMovieFinalAssembly(latest.id)
        .then((next) =>
          setAssemblies((current) =>
            current.map((item) => (item.id === next.id ? next : item)),
          ),
        )
        .catch(() => undefined);
    }, 3000);
    return () => clearInterval(timer);
  }, [latest]);

  async function queue() {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      const created = await api.queueMovieFinalAssembly(project.id, {
        resolutionProfile: profile,
        includeApprovedSoundtrackCues,
      });
      setAssemblies((current) => [created, ...current]);
      setMessage(text("assemblyQueued"));
    } catch (cause) {
      setError(
        cause instanceof Error ? cause.message : text("assemblyQueueError"),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div
      className="movie-exports-workspace"
      data-testid="movie-exports-workspace"
    >
      <section
        className="movie-exports-command"
        aria-labelledby="movie-exports-title"
      >
        <div>
          <span className="movie-workspace-kicker">
            {t("movieExports.commandEyebrow")}
          </span>
          <h3 id="movie-exports-title">{t("movieExports.commandTitle")}</h3>
          <p>{t("movieExports.commandText")}</p>
        </div>
        <div className="movie-exports-command-mark">
          <Download size={23} />
          <span>{t("movieExports.privateMaster")}</span>
        </div>
      </section>

      <section
        className="movie-exports-summary"
        aria-label={t("movieExports.readiness")}
      >
        <ExportMetric
          locale={locale}
          label={text("summaryShots")}
          value={shots.length}
          detail={text("inProject")}
        />
        <ExportMetric
          locale={locale}
          label={text("summaryTakes")}
          value={takes.length}
          detail={text("generatedFootage")}
        />
        <ExportMetric
          locale={locale}
          label={text("summarySelected")}
          value={selected.length}
          detail={text("carriedToMaster")}
        />
        <ExportMetric
          locale={locale}
          label={text("summaryFinalized")}
          value={finalized.length}
          detail={text("lockedForAssembly")}
        />
      </section>

      <section className="movie-exports-boundary">
        <ShieldCheck size={16} />
        <div>
          <strong>{text("providerNeutralDelivery")}</strong>
          <span>{text("providerNeutralDescription")}</span>
        </div>
      </section>

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
          <button
            type="button"
            onClick={() => setError("")}
            aria-label={text("dismissError")}
          >
            <X size={13} />
          </button>
        </div>
      )}

      <section
        className="movie-exports-queue"
        aria-labelledby="movie-exports-queue-title"
      >
        <div className="movie-exports-section-heading">
          <div>
            <span className="movie-workspace-kicker">{text("newExport")}</span>
            <h3 id="movie-exports-queue-title">
              {text("chooseDeliveryProfile")}
            </h3>
            <p>
              {readyToAssemble
                ? text("reviewableTakes", {
                    count: formatMovieDeliveryNumber(
                      locale,
                      finalized.length || selected.length,
                    ),
                  })
                : text("noTakeSelected")}
            </p>
          </div>
          <button
            type="button"
            className="movie-workspace-button is-quiet"
            onClick={() => void load()}
          >
            <RefreshCw size={13} /> {t("movieExports.refresh")}
          </button>
        </div>
        <div
          className="movie-exports-profiles"
          role="radiogroup"
          aria-label={text("deliveryProfile")}
        >
          {profiles.map((item) => (
            <button
              key={item.id}
              type="button"
              role="radio"
              aria-checked={profile === item.id}
              className={profile === item.id ? "is-active" : ""}
              onClick={() => setProfile(item.id)}
            >
              <strong>{item.label}</strong>
              <small>
                {" "}
                {item.id === "hd-1080p" ? "1920 × 1080" : "3840 × 2160"} ·{" "}
                {text(item.detailKey)}
              </small>
            </button>
          ))}
        </div>
        <div className="movie-exports-actions">
          <label className="movie-exports-soundtrack-toggle">
            <input
              type="checkbox"
              checked={includeApprovedSoundtrackCues}
              onChange={(event) =>
                setIncludeApprovedSoundtrackCues(event.target.checked)
              }
            />
            <span>
              <strong>{t("movieBody.audio.approvedCues")}</strong>
              <small>{t("movieBody.audio.approvedCuesHint")}</small>
            </span>
          </label>
          <button
            type="button"
            className="movie-workspace-button is-primary"
            onClick={() => void queue()}
            disabled={busy || !readyToAssemble}
          >
            {busy ? t("movieExports.queue") : t("movieExports.assemble")}
          </button>
          {!readyToAssemble && (
            <span>
              {text("selectFinalizeFirst")} {text("serverRechecks")}
            </span>
          )}
        </div>
      </section>

      <section
        className="movie-exports-list"
        aria-labelledby="movie-exports-list-title"
      >
        <div className="movie-exports-section-heading">
          <div>
            <span className="movie-workspace-kicker">
              {text("exportHistory")}
            </span>
            <h3 id="movie-exports-list-title">
              {text("everyMasterReviewable")}
            </h3>
            <p>{text("completedMastersPrivate")}</p>
          </div>
          <Film size={18} />
        </div>
        {loading ? (
          <div className="movie-exports-empty">
            <Clock3 size={18} />
            <span>{text("loadingRecords")}</span>
          </div>
        ) : assemblies.length === 0 ? (
          <div className="movie-exports-empty">
            <Film size={20} />
            <strong>{text("noExportYet")}</strong>
            <p>{text("recordAfterRequest")}</p>
          </div>
        ) : (
          <div className="movie-exports-cards">
            {assemblies.map((assembly) => (
              <ExportCard
                key={assembly.id}
                assembly={assembly}
                locale={locale}
              />
            ))}
          </div>
        )}
      </section>

      <div className="movie-exports-footnote">
        <Clock3 size={14} />
        <span>
          {text("exportFootnote")} {text("exportTerminalStep")}
        </span>
      </div>
    </div>
  );
}

function ExportCard({
  assembly,
  locale,
}: {
  assembly: MovieFinalAssembly;
  locale: Parameters<typeof movieDeliveryText>[0];
}) {
  const text = (
    key: Parameters<typeof movieDeliveryText>[1],
    variables?: Record<string, string | number>,
  ) => movieDeliveryText(locale, key, variables);
  const tone =
    assembly.status === "Ready"
      ? "is-ready"
      : assembly.status === "Failed"
        ? "is-failed"
        : "is-pending";
  return (
    <article
      className={`movie-exports-card ${tone}`}
      aria-label={`${text("assemblyStatus", { status: movieDeliveryStatusLabel(locale, assembly.status) })}`}
    >
      <header className="movie-exports-card-header">
        <div>
          <span className="movie-exports-card-kicker">
            {assembly.resolutionProfile} · {assembly.outputWidth} ×{" "}
            {assembly.outputHeight}
          </span>
          <h4>
            {assembly.status === "Ready"
              ? text("masterReady")
              : text("assemblyStatus", {
                  status: movieDeliveryStatusLabel(locale, assembly.status),
                })}
          </h4>
          <p>
            {formatMovieDeliveryNumber(locale, assembly.timelineItemCount)}{" "}
            {text("timelineItems")} ·{" "}
            {formatMovieDeliveryNumber(locale, assembly.audioMixInputCount)}{" "}
            {text("audioInputs")} · {text("captions")}{" "}
            {movieDeliveryCaptionModeLabel(locale, assembly.captionsMode)}
          </p>
        </div>
        <span className={`movie-exports-status ${tone}`}>
          {assembly.status === "Ready" ? (
            <CheckCircle2 size={12} />
          ) : (
            <Clock3 size={12} />
          )}
          {movieDeliveryStatusLabel(locale, assembly.status)}
        </span>
      </header>
      <div
        className="movie-exports-progress"
        aria-label={text("assemblyProgress")}
      >
        <div
          style={{
            width: `${Math.max(0, Math.min(100, assembly.progressPercent))}%`,
          }}
        />
      </div>
      <dl className="movie-exports-facts">
        <div>
          <dt>{text("qualityGate")}</dt>
          <dd>{movieDeliveryStatusLabel(locale, assembly.qcStatus)}</dd>
        </div>
        <div>
          <dt>{text("sources")}</dt>
          <dd>
            {text("takesCount", {
              count: formatMovieDeliveryNumber(
                locale,
                assembly.sourceTakeIds.length,
              ),
            })}
          </dd>
        </div>
        <div>
          <dt>{text("attempts")}</dt>
          <dd>{formatMovieDeliveryNumber(locale, assembly.attemptCount)}</dd>
        </div>
        <div>
          <dt>{text("completed")}</dt>
          <dd>
            {assembly.completedAt
              ? formatMovieDeliveryDate(locale, assembly.completedAt)
              : "—"}
          </dd>
        </div>
      </dl>
      <footer className="movie-exports-card-actions">
        {assembly.outputAssetId ? (
          <a
            className="movie-workspace-button is-primary"
            href={api.movieFinalAssemblyDownloadUrl(assembly.id)}
            download
          >
            <Download size={13} /> {text("downloadMaster")}
          </a>
        ) : (
          <span>
            {assembly.status === "Failed"
              ? text("failedOutput")
              : text("stillProducing")}
          </span>
        )}
      </footer>
    </article>
  );
}

function ExportMetric({
  locale,
  label,
  value,
  detail,
}: {
  locale: Parameters<typeof movieDeliveryText>[0];
  label: string;
  value: number;
  detail: string;
}) {
  return (
    <div className="movie-exports-metric">
      <span>{label}</span>
      <strong>{formatMovieDeliveryNumber(locale, value)}</strong>
      <small>{detail}</small>
    </div>
  );
}
