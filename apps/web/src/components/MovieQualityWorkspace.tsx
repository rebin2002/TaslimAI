"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, CheckCircle2, Clock3, RefreshCw, ShieldCheck, ShieldX, X } from "lucide-react";
import {
  api,
  type MovieFinalAssembly,
  type MovieProductionCheckpoint,
  type MovieProductionContinuityReview,
  type MovieProject,
} from "@/lib/api";

/**
 * The review gate that stands between an in-progress production and a delivered
 * master. It combines three real signals: persisted production checkpoints,
 * continuity review findings, and the deterministic quality gate recorded on
 * each final assembly.
 */
export function MovieQualityWorkspace({ project }: { project: MovieProject }) {
  const [checkpoint, setCheckpoint] = useState<MovieProductionCheckpoint | null>(null);
  const [continuity, setContinuity] = useState<MovieProductionContinuityReview | null>(null);
  const [assemblies, setAssemblies] = useState<MovieFinalAssembly[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  const shots = useMemo(() => project.scenes.flatMap((scene) => scene.shots ?? []), [project]);
  const takes = useMemo(() => shots.flatMap((shot) => shot.takes ?? []), [shots]);

  const load = useCallback(async () => {
    setLoading(true);
    const [checkpointResult, continuityResult, assemblyResults] = await Promise.all([
      api.getMovieProductionCheckpoint(project.id).catch(() => null),
      api.reviewMovieProductionContinuity(project.id).catch(() => null),
      Promise.all((project.assemblies ?? []).map((item) => api.getMovieFinalAssembly(item.id).catch(() => null))),
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

  const blockingFindings = (continuity?.findings ?? []).filter((finding) => finding.severity === "error");
  const advisoryFindings = (continuity?.findings ?? []).filter((finding) => finding.severity !== "error");
  const unselectedShots = useMemo(
    () => shots.filter((shot) => !(shot.takes ?? []).some((take) => take.selectedAt)),
    [shots],
  );
  const approvedTakes = takes.filter((take) => take.approvals.some((approval) => approval.decision === "Approved"));
  const masteredAssembly = assemblies.find((assembly) => assembly.status === "Ready") ?? null;
  const failedAssembly = assemblies.find((assembly) => assembly.status === "Failed") ?? null;

  const deliverable =
    shots.length > 0 && unselectedShots.length === 0 && blockingFindings.length === 0 && Boolean(masteredAssembly);

  return (
    <div className="movie-qc-workspace" data-testid="movie-qc-workspace">
      <section className="movie-qc-command" aria-labelledby="movie-qc-title">
        <div>
          <span className="movie-workspace-kicker">Quality control · delivery gate</span>
          <h3 id="movie-qc-title">Check before you deliver.</h3>
          <p>
            QC is a review gate over persisted evidence: production checkpoints, continuity findings and the
            deterministic quality result recorded on the assembled master. Nothing here is simulated—if a check has no
            evidence, it is reported as unverified.
          </p>
        </div>
        <div className="movie-qc-command-mark">
          <ShieldCheck size={23} />
          <span>REVIEW GATE</span>
        </div>
      </section>

      <section className="movie-qc-summary" aria-label="Quality summary">
        <QcMetric label="Shots" value={shots.length} detail="in the cut" />
        <QcMetric label="Selected takes" value={shots.length - unselectedShots.length} detail="carried forward" />
        <QcMetric label="Blocking findings" value={blockingFindings.length} detail="continuity errors" />
        <QcMetric label="Masters ready" value={assemblies.filter((assembly) => assembly.status === "Ready").length} detail="passed the gate" />
      </section>

      <section className={`movie-qc-verdict ${deliverable ? "is-ready" : "is-blocked"}`} aria-label="Delivery verdict">
        {deliverable ? <CheckCircle2 size={18} /> : <ShieldX size={18} />}
        <div>
          <strong>{deliverable ? "Ready for delivery" : "Not ready for delivery"}</strong>
          <span>
            {deliverable
              ? "Every shot points at a selected take, no blocking continuity finding is open, and a master has passed the quality gate."
              : firstBlocker(unselectedShots.length, blockingFindings.length, masteredAssembly, failedAssembly)}
          </span>
        </div>
        <button type="button" className="movie-workspace-button is-quiet" onClick={() => void load()}>
          <RefreshCw size={13} /> Re-run checks
        </button>
      </section>

      {error && (
        <div className="movie-selects-notice is-error" role="alert">
          <AlertTriangle size={15} />
          <span>{error}</span>
          <button type="button" onClick={() => setError("")} aria-label="Dismiss error">
            <X size={13} />
          </button>
        </div>
      )}

      <section className="movie-qc-section" aria-labelledby="movie-qc-checkpoint-title">
        <div className="movie-qc-section-heading">
          <div>
            <span className="movie-workspace-kicker">Production checkpoint</span>
            <h3 id="movie-qc-checkpoint-title">What is actually complete.</h3>
            <p>Derived from persisted shot state and generation jobs—not from optimistic UI state.</p>
          </div>
          {loading ? <Clock3 size={18} /> : <strong className="movie-qc-progress">{checkpoint?.progressPercent ?? 0}%</strong>}
        </div>
        {!checkpoint || checkpoint.totalShots === 0 ? (
          <div className="movie-qc-empty">
            <Clock3 size={20} />
            <strong>No checkpoint evidence yet</strong>
            <p>A checkpoint appears once this project has real shots and generation activity.</p>
          </div>
        ) : (
          <>
            <div className="movie-qc-progress-bar" aria-label="Production progress">
              <div style={{ width: `${Math.max(0, Math.min(100, checkpoint.progressPercent))}%` }} />
            </div>
            <ul className="movie-qc-checkpoint-facts">
              <li>
                <span>State</span>
                <strong>{checkpoint.state}</strong>
              </li>
              <li>
                <span>Complete</span>
                <strong>{checkpoint.completedShots}/{checkpoint.totalShots}</strong>
              </li>
              <li>
                <span>Running</span>
                <strong>{checkpoint.runningShots}</strong>
              </li>
              <li>
                <span>Blocked</span>
                <strong>{checkpoint.blockedShots}</strong>
              </li>
              <li>
                <span>Recoverable</span>
                <strong>{checkpoint.recoverableShots}</strong>
              </li>
              <li>
                <span>Awaiting approval</span>
                <strong>{checkpoint.pendingApprovalShots}</strong>
              </li>
            </ul>
            {checkpoint.items.some((item) => item.state === "Blocked" || item.state === "Recoverable") && (
              <div className="movie-qc-blocked-list">
                {checkpoint.items
                  .filter((item) => item.state === "Blocked" || item.state === "Recoverable")
                  .slice(0, 8)
                  .map((item) => (
                    <div key={item.shotId} className="movie-qc-blocked-item">
                      <span>
                        Scene {String(item.sceneSequence).padStart(2, "0")} · Shot {String(item.shotSequence).padStart(2, "0")}
                      </span>
                      <strong>{item.label}</strong>
                      <small>{item.blockedReason ?? item.nextAction ?? item.state}</small>
                    </div>
                  ))}
              </div>
            )}
          </>
        )}
      </section>

      <section className="movie-qc-section" aria-labelledby="movie-qc-continuity-title">
        <div className="movie-qc-section-heading">
          <div>
            <span className="movie-workspace-kicker">Continuity review</span>
            <h3 id="movie-qc-continuity-title">Findings with evidence.</h3>
            <p>
              {continuity
                ? `Assembled ${new Date(continuity.assembledAt).toLocaleString()} · review only`
                : "Continuity review is unavailable for this project right now."}
            </p>
          </div>
          <AlertTriangle size={18} />
        </div>
        {(continuity?.findings ?? []).length === 0 ? (
          <div className="movie-qc-empty is-clear">
            <CheckCircle2 size={20} />
            <strong>No continuity findings</strong>
            <p>The continuity review did not report an open issue for this project.</p>
          </div>
        ) : (
          <div className="movie-qc-findings">
            {[...blockingFindings, ...advisoryFindings].slice(0, 12).map((finding, index) => (
              <article key={`${finding.category}-${index}`} className={`movie-qc-finding is-${finding.severity}`}>
                <header>
                  <span className="movie-qc-finding-type">{finding.category}</span>
                  <span className={`movie-qc-severity is-${finding.severity}`}>{finding.severity}</span>
                </header>
                <p>{finding.explanation}</p>
                <small>
                  {finding.affectedTarget.label ?? finding.affectedTarget.targetType}
                  {finding.uncertainty ? ` · ${finding.uncertainty}` : ""}
                </small>
                {finding.suggestedCorrection && <em>{finding.suggestedCorrection}</em>}
              </article>
            ))}
          </div>
        )}
      </section>

      <section className="movie-qc-section" aria-labelledby="movie-qc-gate-title">
        <div className="movie-qc-section-heading">
          <div>
            <span className="movie-workspace-kicker">Delivery gate</span>
            <h3 id="movie-qc-gate-title">Output checks on the master.</h3>
            <p>The quality gate is recorded on each assembly by the encoding job.</p>
          </div>
          <ShieldCheck size={18} />
        </div>
        {assemblies.length === 0 ? (
          <div className="movie-qc-empty">
            <ShieldCheck size={20} />
            <strong>No master has been assembled</strong>
            <p>Queue a final assembly in Exports; its quality result appears here.</p>
          </div>
        ) : (
          <ul className="movie-qc-gate-list">
            {assemblies.map((assembly) => (
              <li key={assembly.id} className={assembly.qcStatus === "Passed" ? "is-pass" : assembly.status === "Failed" ? "is-fail" : "is-pending"}>
                <div>
                  <strong>
                    {assembly.resolutionProfile} · {assembly.outputWidth} × {assembly.outputHeight}
                  </strong>
                  <small>
                    {assembly.status} · {assembly.sourceTakeIds.length} source take(s) ·{" "}
                    {assembly.completedAt ? new Date(assembly.completedAt).toLocaleString() : "in progress"}
                  </small>
                </div>
                <span>{assembly.qcStatus}</span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="movie-qc-section" aria-labelledby="movie-qc-takes-title">
        <div className="movie-qc-section-heading">
          <div>
            <span className="movie-workspace-kicker">Take evidence</span>
            <h3 id="movie-qc-takes-title">Approval state per shot.</h3>
            <p>{approvedTakes.length} take(s) carry an approval decision. Selects evidence lives in the Selects room.</p>
          </div>
        </div>
        {shots.length === 0 ? (
          <div className="movie-qc-empty">
            <span>No shots have been planned yet.</span>
          </div>
        ) : (
          <div className="movie-qc-shot-list">
            {shots.slice(0, 24).map((shot) => {
              const shotTakes = shot.takes ?? [];
              const chosen = shotTakes.find((take) => take.selectedAt) ?? null;
              return (
                <div key={shot.id} className="movie-qc-shot-row">
                  <span className="movie-qc-shot-index">#{String(shot.sequence).padStart(2, "0")}</span>
                  <div>
                    <strong>{shot.description}</strong>
                    <small>
                      {shotTakes.length} take(s)
                      {chosen ? ` · selected: ${chosen.label}` : " · no selected take"}
                    </small>
                  </div>
                  <span className={chosen ? "is-ok" : "is-missing"}>{chosen ? "Selected" : "Unselected"}</span>
                </div>
              );
            })}
          </div>
        )}
      </section>

      <div className="movie-qc-footnote">
        <Clock3 size={14} />
        <span>
          QC is advisory review data. It never blocks a request silently—the delivery verdict is shown to the customer
          so the next action stays explicit.
        </span>
      </div>
    </div>
  );
}

function firstBlocker(
  unselectedShots: number,
  blockingFindings: number,
  mastered: MovieFinalAssembly | null,
  failed: MovieFinalAssembly | null,
) {
  if (unselectedShots > 0) return `${unselectedShots} shot(s) still have no selected take. Finish the cut in Production.`;
  if (blockingFindings > 0) return `${blockingFindings} blocking continuity finding(s) remain open.`;
  if (failed) return "The most recent assembly did not pass the quality gate. Review the sources and export again.";
  if (!mastered) return "No master has passed the quality gate yet. Assemble an export in the Exports room.";
  return "The delivery gate has not been satisfied yet.";
}

function QcMetric({ label, value, detail }: { label: string; value: number; detail: string }) {
  return (
    <div className="movie-qc-metric">
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{detail}</small>
    </div>
  );
}