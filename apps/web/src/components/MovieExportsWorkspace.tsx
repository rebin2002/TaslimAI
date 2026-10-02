"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, CheckCircle2, Clock3, Download, Film, RefreshCw, ShieldCheck, X } from "lucide-react";
import { api, type MovieFinalAssembly, type MovieProject } from "@/lib/api";

const profiles = [
  { id: "hd-1080p", label: "HD 1080p", detail: "1920 × 1080 · fastest delivery" },
  { id: "uhd-4k", label: "UHD 4K", detail: "3840 × 2160 · master delivery" },
];

const terminalStatuses = new Set(["Ready", "Failed", "Cancelled"]);

/**
 * The terminal step of the customer workflow: turn the approved, selected takes
 * into one durable private master and hand the customer a real download. Every
 * value shown here comes from the persisted final assembly record.
 */
export function MovieExportsWorkspace({ project }: { project: MovieProject }) {
  const [assemblies, setAssemblies] = useState<MovieFinalAssembly[]>([]);
  const [profile, setProfile] = useState("hd-1080p");
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const shots = useMemo(() => project.scenes.flatMap((scene) => scene.shots ?? []), [project]);
  const takes = useMemo(() => shots.flatMap((shot) => shot.takes ?? []), [shots]);
  const finalized = useMemo(() => takes.filter((take) => take.finalizedAt), [takes]);
  const selected = useMemo(() => takes.filter((take) => take.selectedAt), [takes]);
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
        .then((next) => setAssemblies((current) => current.map((item) => (item.id === next.id ? next : item))))
        .catch(() => undefined);
    }, 3000);
    return () => clearInterval(timer);
  }, [latest]);

  async function queue() {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      const created = await api.queueMovieFinalAssembly(project.id, { resolutionProfile: profile });
      setAssemblies((current) => [created, ...current]);
      setMessage("Final assembly queued. The master is produced from the approved, selected takes already in this project.");
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "The final assembly could not be queued.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="movie-exports-workspace" data-testid="movie-exports-workspace">
      <section className="movie-exports-command" aria-labelledby="movie-exports-title">
        <div>
          <span className="movie-workspace-kicker">Exports · final assembly</span>
          <h3 id="movie-exports-title">Deliver one real master.</h3>
          <p>
            Final assembly reads the takes that are already approved, selected and finalized in this project, encodes
            a single private master, runs deterministic output checks, and publishes a downloadable asset. Nothing is
            fabricated when the project is not ready.
          </p>
        </div>
        <div className="movie-exports-command-mark">
          <Download size={23} />
          <span>PRIVATE MASTER</span>
        </div>
      </section>

      <section className="movie-exports-summary" aria-label="Export readiness">
        <ExportMetric label="Shots" value={shots.length} detail="in this project" />
        <ExportMetric label="Takes" value={takes.length} detail="generated footage" />
        <ExportMetric label="Selected" value={selected.length} detail="carried to the master" />
        <ExportMetric label="Finalized" value={finalized.length} detail="locked for assembly" />
      </section>

      <section className="movie-exports-boundary">
        <ShieldCheck size={16} />
        <div>
          <strong>Provider-neutral delivery</strong>
          <span>
            The export is produced locally from persisted sources. No provider URL, storage key or internal model name
            is ever exposed to the browser.
          </span>
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
          <button type="button" onClick={() => setError("")} aria-label="Dismiss error">
            <X size={13} />
          </button>
        </div>
      )}

      <section className="movie-exports-queue" aria-labelledby="movie-exports-queue-title">
        <div className="movie-exports-section-heading">
          <div>
            <span className="movie-workspace-kicker">New export</span>
            <h3 id="movie-exports-queue-title">Choose a delivery profile.</h3>
            <p>
              {readyToAssemble
                ? `${finalized.length || selected.length} reviewable take(s) are available to assemble.`
                : "No take has been selected yet. Approve and select a take in Production before exporting."}
            </p>
          </div>
          <button type="button" className="movie-workspace-button is-quiet" onClick={() => void load()}>
            <RefreshCw size={13} /> Refresh
          </button>
        </div>
        <div className="movie-exports-profiles" role="radiogroup" aria-label="Delivery profile">
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
              <small>{item.detail}</small>
            </button>
          ))}
        </div>
        <div className="movie-exports-actions">
          <button
            type="button"
            className="movie-workspace-button is-primary"
            onClick={() => void queue()}
            disabled={busy || !readyToAssemble}
          >
            {busy ? "Queueing export…" : "Assemble and export master"}
          </button>
          {!readyToAssemble && <span>Select and finalize a take first. The server re-checks every source before encoding.</span>}
        </div>
      </section>

      <section className="movie-exports-list" aria-labelledby="movie-exports-list-title">
        <div className="movie-exports-section-heading">
          <div>
            <span className="movie-workspace-kicker">Export history</span>
            <h3 id="movie-exports-list-title">Every master stays reviewable.</h3>
            <p>Completed masters remain downloadable private assets. Failed attempts keep their diagnostic state.</p>
          </div>
          <Film size={18} />
        </div>
        {loading ? (
          <div className="movie-exports-empty">
            <Clock3 size={18} />
            <span>Loading export records…</span>
          </div>
        ) : assemblies.length === 0 ? (
          <div className="movie-exports-empty">
            <Film size={20} />
            <strong>No export yet</strong>
            <p>A record appears here after a real final assembly has been requested for this project.</p>
          </div>
        ) : (
          <div className="movie-exports-cards">
            {assemblies.map((assembly) => (
              <ExportCard key={assembly.id} assembly={assembly} />
            ))}
          </div>
        )}
      </section>

      <div className="movie-exports-footnote">
        <Clock3 size={14} />
        <span>
          Export is the terminal step. A master is only marked Ready after the encoded output passes the deterministic
          quality gate and the private asset is durably published.
        </span>
      </div>
    </div>
  );
}

function ExportCard({ assembly }: { assembly: MovieFinalAssembly }) {
  const tone = assembly.status === "Ready" ? "is-ready" : assembly.status === "Failed" ? "is-failed" : "is-pending";
  return (
    <article className={`movie-exports-card ${tone}`} aria-label={`Export ${assembly.status}`}>
      <header className="movie-exports-card-header">
        <div>
          <span className="movie-exports-card-kicker">
            {assembly.resolutionProfile} · {assembly.outputWidth} × {assembly.outputHeight}
          </span>
          <h4>{assembly.status === "Ready" ? "Master ready" : `Assembly ${assembly.status.toLowerCase()}`}</h4>
          <p>
            {assembly.timelineItemCount} timeline item(s) · {assembly.audioMixInputCount} audio input(s) · captions{" "}
            {assembly.captionsMode.toLowerCase()}
          </p>
        </div>
        <span className={`movie-exports-status ${tone}`}>
          {assembly.status === "Ready" ? <CheckCircle2 size={12} /> : <Clock3 size={12} />}
          {assembly.status}
        </span>
      </header>
      <div className="movie-exports-progress" aria-label="Assembly progress">
        <div style={{ width: `${Math.max(0, Math.min(100, assembly.progressPercent))}%` }} />
      </div>
      <dl className="movie-exports-facts">
        <div>
          <dt>Quality gate</dt>
          <dd>{assembly.qcStatus}</dd>
        </div>
        <div>
          <dt>Sources</dt>
          <dd>{assembly.sourceTakeIds.length} take(s)</dd>
        </div>
        <div>
          <dt>Attempts</dt>
          <dd>{assembly.attemptCount}</dd>
        </div>
        <div>
          <dt>Completed</dt>
          <dd>{assembly.completedAt ? new Date(assembly.completedAt).toLocaleString() : "—"}</dd>
        </div>
      </dl>
      <footer className="movie-exports-card-actions">
        {assembly.outputAssetId ? (
          <a className="movie-workspace-button is-primary" href={api.movieFinalAssemblyDownloadUrl(assembly.id)} download>
            <Download size={13} /> Download master
          </a>
        ) : (
          <span>
            {assembly.status === "Failed"
              ? "This attempt did not publish an output. Re-run the export after correcting the sources."
              : "The master is still being produced. Download unlocks when the asset is published."}
          </span>
        )}
      </footer>
    </article>
  );
}

function ExportMetric({ label, value, detail }: { label: string; value: number; detail: string }) {
  return (
    <div className="movie-exports-metric">
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{detail}</small>
    </div>
  );
}
