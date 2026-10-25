"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import {
  AlertTriangle,
  CheckCircle2,
  Clock3,
  RefreshCw,
  ShieldCheck,
  ShieldX,
  X,
} from "lucide-react";
import {
  api,
  type MovieFinalAssembly,
  type MovieProductionCheckpoint,
  type MovieProductionContinuityReview,
  type MovieProject,
} from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";
import {
  formatMovieDeliveryDate,
  formatMovieDeliveryNumber,
  movieDeliveryStatusLabel,
  movieDeliveryText,
} from "@/lib/movieDeliveryI18n";

/**
 * The review gate that stands between an in-progress production and a delivered
 * master. It combines three real signals: persisted production checkpoints,
 * continuity review findings, and the deterministic quality gate recorded on
 * each final assembly.
 */
export function MovieQualityWorkspace({ project }: { project: MovieProject }) {
  const { locale, t } = useLocale();
  const text = (
    key: Parameters<typeof movieDeliveryText>[1],
    variables?: Record<string, string | number>,
  ) => movieDeliveryText(locale, key, variables);
  const [checkpoint, setCheckpoint] =
    useState<MovieProductionCheckpoint | null>(null);
  const [continuity, setContinuity] =
    useState<MovieProductionContinuityReview | null>(null);
  const [assemblies, setAssemblies] = useState<MovieFinalAssembly[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  const shots = useMemo(
    () => project.scenes.flatMap((scene) => scene.shots ?? []),
    [project],
  );
  const takes = useMemo(
    () => shots.flatMap((shot) => shot.takes ?? []),
    [shots],
  );

  const load = useCallback(async () => {
    setLoading(true);
    const [checkpointResult, continuityResult, assemblyResults] =
      await Promise.all([
        api.getMovieProductionCheckpoint(project.id).catch(() => null),
        api.reviewMovieProductionContinuity(project.id).catch(() => null),
        Promise.all(
          (project.assemblies ?? []).map((item) =>
            api.getMovieFinalAssembly(item.id).catch(() => null),
          ),
        ),
      ]);
    setCheckpoint(checkpointResult);
    setContinuity(continuityResult);
    setAssemblies(
      assemblyResults
        .filter((item): item is MovieFinalAssembly => item !== null)
        .sort((left, right) => right.createdAt.localeCompare(left.createdAt)),
    );
    setLoading(false);
  }, [project.assemblies, project.id]);

  useEffect(() => {
    void Promise.resolve().then(() => load());
  }, [load]);

  const blockingFindings = (continuity?.findings ?? []).filter(
    (finding) => finding.severity === "error",
  );
  const advisoryFindings = (continuity?.findings ?? []).filter(
    (finding) => finding.severity !== "error",
  );
  const unselectedShots = useMemo(
    () =>
      shots.filter(
        (shot) => !(shot.takes ?? []).some((take) => take.selectedAt),
      ),
    [shots],
  );
  const approvedTakes = takes.filter((take) =>
    take.approvals.some((approval) => approval.decision === "Approved"),
  );
  const masteredAssembly =
    assemblies.find((assembly) => assembly.status === "Ready") ?? null;
  const failedAssembly =
    assemblies.find((assembly) => assembly.status === "Failed") ?? null;

  const deliverable =
    shots.length > 0 &&
    unselectedShots.length === 0 &&
    blockingFindings.length === 0 &&
    Boolean(masteredAssembly);

  return (
    <div className="movie-qc-workspace" data-testid="movie-qc-workspace">
      <section className="movie-qc-command" aria-labelledby="movie-qc-title">
        <div>
          <span className="movie-workspace-kicker">
            {t("movieQc.commandEyebrow")}
          </span>
          <h3 id="movie-qc-title">{t("movieQc.commandTitle")}</h3>
          <p>{t("movieQc.commandText")}</p>
        </div>
        <div className="movie-qc-command-mark">
          <ShieldCheck size={23} />
          <span>{t("movieQc.reviewGate")}</span>
        </div>
      </section>

      <section className="movie-qc-summary" aria-label={t("movieQc.summary")}>
        <QcMetric
          locale={locale}
          label={text("summaryShots")}
          value={shots.length}
          detail={text("inProject")}
        />
        <QcMetric
          locale={locale}
          label={text("summarySelected")}
          value={shots.length - unselectedShots.length}
          detail={text("carriedToMaster")}
        />
        <QcMetric
          locale={locale}
          label={text("summaryBlockingFindings")}
          value={blockingFindings.length}
          detail={text("continuityErrors")}
        />
        <QcMetric
          locale={locale}
          label={text("summaryMastersReady")}
          value={
            assemblies.filter((assembly) => assembly.status === "Ready").length
          }
          detail={text("passedGate")}
        />
      </section>

      <section
        className={`movie-qc-verdict ${deliverable ? "is-ready" : "is-blocked"}`}
        aria-label={text("deliveryVerdict")}
      >
        {deliverable ? <CheckCircle2 size={18} /> : <ShieldX size={18} />}
        <div>
          <strong>
            {deliverable
              ? text("readyForDelivery")
              : text("notReadyForDelivery")}
          </strong>
          <span>
            {deliverable
              ? text("readyExplanation")
              : firstBlocker(
                  locale,
                  text,
                  unselectedShots.length,
                  blockingFindings.length,
                  masteredAssembly,
                  failedAssembly,
                )}
          </span>
        </div>
        <button
          type="button"
          className="movie-workspace-button is-quiet"
          onClick={() => void load()}
        >
          <RefreshCw size={13} /> {t("movieQc.rerun")}
        </button>
      </section>

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
        className="movie-qc-section"
        aria-labelledby="movie-qc-checkpoint-title"
      >
        <div className="movie-qc-section-heading">
          <div>
            <span className="movie-workspace-kicker">
              {text("productionCheckpoint")}
            </span>
            <h3 id="movie-qc-checkpoint-title">{text("checkpointTitle")}</h3>
            <p>{text("checkpointText")}</p>
          </div>
          {loading ? (
            <Clock3 size={18} />
          ) : (
            <strong className="movie-qc-progress">
              {formatMovieDeliveryNumber(
                locale,
                checkpoint?.progressPercent ?? 0,
              )}
              %
            </strong>
          )}
        </div>
        {!checkpoint || checkpoint.totalShots === 0 ? (
          <div className="movie-qc-empty">
            <Clock3 size={20} />
            <strong>{text("noCheckpointEvidence")}</strong>
            <p>{text("noCheckpointText")}</p>
          </div>
        ) : (
          <>
            <div
              className="movie-qc-progress-bar"
              aria-label={text("productionProgress")}
            >
              <div
                style={{
                  width: `${Math.max(0, Math.min(100, checkpoint.progressPercent))}%`,
                }}
              />
            </div>
            <ul className="movie-qc-checkpoint-facts">
              <li>
                <span>{text("state")}</span>
                <strong>
                  {movieDeliveryStatusLabel(locale, checkpoint.state)}
                </strong>
              </li>
              <li>
                <span>{text("complete")}</span>
                <strong>
                  {formatMovieDeliveryNumber(locale, checkpoint.completedShots)}
                  /{formatMovieDeliveryNumber(locale, checkpoint.totalShots)}
                </strong>
              </li>
              <li>
                <span>{text("running")}</span>
                <strong>
                  {formatMovieDeliveryNumber(locale, checkpoint.runningShots)}
                </strong>
              </li>
              <li>
                <span>{text("blocked")}</span>
                <strong>
                  {formatMovieDeliveryNumber(locale, checkpoint.blockedShots)}
                </strong>
              </li>
              <li>
                <span>{text("recoverable")}</span>
                <strong>
                  {formatMovieDeliveryNumber(
                    locale,
                    checkpoint.recoverableShots,
                  )}
                </strong>
              </li>
              <li>
                <span>{text("awaitingApproval")}</span>
                <strong>
                  {formatMovieDeliveryNumber(
                    locale,
                    checkpoint.pendingApprovalShots,
                  )}
                </strong>
              </li>
            </ul>
            {checkpoint.items.some(
              (item) =>
                item.state === "Blocked" || item.state === "Recoverable",
            ) && (
              <div className="movie-qc-blocked-list">
                {checkpoint.items
                  .filter(
                    (item) =>
                      item.state === "Blocked" || item.state === "Recoverable",
                  )
                  .slice(0, 8)
                  .map((item) => (
                    <div key={item.shotId} className="movie-qc-blocked-item">
                      <span>
                        {text("sceneShot", {
                          scene: String(item.sceneSequence).padStart(2, "0"),
                          shot: String(item.shotSequence).padStart(2, "0"),
                        })}
                      </span>
                      <strong>{item.label}</strong>
                      <small>
                        {item.blockedReason ?? item.nextAction ?? item.state}
                      </small>
                    </div>
                  ))}
              </div>
            )}
          </>
        )}
      </section>

      <section
        className="movie-qc-section"
        aria-labelledby="movie-qc-continuity-title"
      >
        <div className="movie-qc-section-heading">
          <div>
            <span className="movie-workspace-kicker">
              {text("continuityReview")}
            </span>
            <h3 id="movie-qc-continuity-title">
              {text("findingsWithEvidence")}
            </h3>
            <p>
              {continuity
                ? text("assembledReviewOnly", {
                    time: formatMovieDeliveryDate(
                      locale,
                      continuity.assembledAt,
                    ),
                  })
                : text("continuityUnavailable")}
            </p>
          </div>
          <AlertTriangle size={18} />
        </div>
        {(continuity?.findings ?? []).length === 0 ? (
          <div className="movie-qc-empty is-clear">
            <CheckCircle2 size={20} />
            <strong>{text("noContinuityFindings")}</strong>
            <p>{text("continuityNoOpenIssue")}</p>
          </div>
        ) : (
          <div className="movie-qc-findings">
            {[...blockingFindings, ...advisoryFindings]
              .slice(0, 12)
              .map((finding, index) => (
                <article
                  key={`${finding.category}-${index}`}
                  className={`movie-qc-finding is-${finding.severity}`}
                >
                  <header>
                    <span className="movie-qc-finding-type">
                      {finding.category}
                    </span>
                    <span
                      className={`movie-qc-severity is-${finding.severity}`}
                    >
                      {finding.severity}
                    </span>
                  </header>
                  <p>{finding.explanation}</p>
                  <small>
                    {finding.affectedTarget.label ??
                      finding.affectedTarget.targetType}
                    {finding.uncertainty ? ` · ${finding.uncertainty}` : ""}
                  </small>
                  {finding.suggestedCorrection && (
                    <em>{finding.suggestedCorrection}</em>
                  )}
                </article>
              ))}
          </div>
        )}
      </section>

      <section
        className="movie-qc-section"
        aria-labelledby="movie-qc-gate-title"
      >
        <div className="movie-qc-section-heading">
          <div>
            <span className="movie-workspace-kicker">
              {text("deliveryGate")}
            </span>
            <h3 id="movie-qc-gate-title">{text("outputChecks")}</h3>
            <p>{text("qualityGateRecorded")}</p>
          </div>
          <ShieldCheck size={18} />
        </div>
        {assemblies.length === 0 ? (
          <div className="movie-qc-empty">
            <ShieldCheck size={20} />
            <strong>{text("noMasterAssembled")}</strong>
            <p>{text("queueFinalAssembly")}</p>
          </div>
        ) : (
          <ul className="movie-qc-gate-list">
            {assemblies.map((assembly) => (
              <li
                key={assembly.id}
                className={
                  assembly.qcStatus === "Passed"
                    ? "is-pass"
                    : assembly.status === "Failed"
                      ? "is-fail"
                      : "is-pending"
                }
              >
                <div>
                  <strong>
                    {assembly.resolutionProfile} · {assembly.outputWidth} ×{" "}
                    {assembly.outputHeight}
                  </strong>
                  <small>
                    {movieDeliveryStatusLabel(locale, assembly.status)} ·{" "}
                    {formatMovieDeliveryNumber(
                      locale,
                      assembly.sourceTakeIds.length,
                    )}{" "}
                    {text("sources")} ·{" "}
                    {assembly.completedAt
                      ? formatMovieDeliveryDate(locale, assembly.completedAt)
                      : movieDeliveryStatusLabel(locale, "InProgress")}
                  </small>
                </div>
                <span>
                  {movieDeliveryStatusLabel(locale, assembly.qcStatus)}
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section
        className="movie-qc-section"
        aria-labelledby="movie-qc-takes-title"
      >
        <div className="movie-qc-section-heading">
          <div>
            <span className="movie-workspace-kicker">
              {text("takeEvidence")}
            </span>
            <h3 id="movie-qc-takes-title">{text("approvalStatePerShot")}</h3>
            <p>
              {text("approvalSummary", {
                count: formatMovieDeliveryNumber(locale, approvedTakes.length),
              })}{" "}
              {text("selectsEvidence")}
            </p>
          </div>
        </div>
        {shots.length === 0 ? (
          <div className="movie-qc-empty">
            <span>{text("noShotsPlanned")}</span>
          </div>
        ) : (
          <div className="movie-qc-shot-list">
            {shots.slice(0, 24).map((shot) => {
              const shotTakes = shot.takes ?? [];
              const chosen = shotTakes.find((take) => take.selectedAt) ?? null;
              return (
                <div key={shot.id} className="movie-qc-shot-row">
                  <span className="movie-qc-shot-index">
                    #{String(shot.sequence).padStart(2, "0")}
                  </span>
                  <div>
                    <strong>{shot.description}</strong>
                    <small>
                      {text("takesCount", {
                        count: formatMovieDeliveryNumber(
                          locale,
                          shotTakes.length,
                        ),
                      })}
                      {chosen
                        ? ` · ${text("selectedTake")}: ${chosen.label}`
                        : ` · ${text("unselectedTake")}`}
                    </small>
                  </div>
                  <span className={chosen ? "is-ok" : "is-missing"}>
                    {chosen ? text("selectedTake") : text("unselectedTake")}
                  </span>
                </div>
              );
            })}
          </div>
        )}
      </section>

      <div className="movie-qc-footnote">
        <Clock3 size={14} />
        <span>{text("qcFootnote")}</span>
      </div>
    </div>
  );
}

function firstBlocker(
  locale: Parameters<typeof movieDeliveryText>[0],
  text: (
    key: Parameters<typeof movieDeliveryText>[1],
    variables?: Record<string, string | number>,
  ) => string,
  unselectedShots: number,
  blockingFindings: number,
  mastered: MovieFinalAssembly | null,
  failed: MovieFinalAssembly | null,
) {
  if (unselectedShots > 0)
    return text("shotNeedsSelectedTake", {
      count: formatMovieDeliveryNumber(locale, unselectedShots),
    });
  if (blockingFindings > 0)
    return text("blockingFindingsRemain", {
      count: formatMovieDeliveryNumber(locale, blockingFindings),
    });
  if (failed) return text("failedAssembly");
  if (!mastered) return text("noMasterPassed");
  return text("deliveryGateUnsatisfied");
}

function QcMetric({
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
    <div className="movie-qc-metric">
      <span>{label}</span>
      <strong>{formatMovieDeliveryNumber(locale, value)}</strong>
      <small>{detail}</small>
    </div>
  );
}
