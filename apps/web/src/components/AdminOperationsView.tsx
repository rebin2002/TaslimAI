"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { Activity, ArrowLeft, Boxes, CircleDollarSign, Database, FileStack, Gauge, RefreshCw, ShieldCheck, UsersRound, Workflow } from "lucide-react";
import { api, type AdminCountBreakdown, type AdminOperationsDashboard } from "@/lib/api";
import { presetRange } from "@/lib/adminUsageState";

function money(value: number) {
  return new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 4 }).format(value);
}

function dateInputValue(value: Date) {
  return value.toISOString().slice(0, 10);
}

function dateTime(value: string | null) {
  return value ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)) : "No completed generation recorded";
}

function bytes(value: number) {
  if (value < 1024) return `${value} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let next = value;
  let unit = -1;
  while (next >= 1024 && unit < units.length - 1) { next /= 1024; unit += 1; }
  return `${next.toFixed(next >= 10 ? 1 : 2)} ${units[unit]}`;
}

function CountList({ items, empty = "No records in this period." }: Readonly<{ items: AdminCountBreakdown[]; empty?: string }>) {
  if (!items.length) return <p className="operations-empty">{empty}</p>;
  return <div className="operations-count-list">{items.map((item) => <div key={item.key}><span>{item.key}</span><strong>{item.count.toLocaleString()}</strong></div>)}</div>;
}

export function AdminOperationsView() {
  const [dashboard, setDashboard] = useState<AdminOperationsDashboard | null>(null);
  const [preset, setPreset] = useState<"today" | "sevenDays" | "thirtyDays" | "custom">("thirtyDays");
  const [from, setFrom] = useState(() => dateInputValue(new Date(Date.now() - 29 * 86_400_000)));
  const [to, setTo] = useState(() => dateInputValue(new Date()));
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (range: { fromUtc: string; toUtc: string }) => {
    setLoading(true);
    setError(null);
    try {
      setDashboard(await api.getAdminOperationsDashboard(range));
    } catch {
      setDashboard(null);
      setError("Operations data could not be loaded. Administrator access is required.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load(presetRange("thirtyDays"));
  }, [load]);

  const applyPreset = (next: "today" | "sevenDays" | "thirtyDays") => {
    setPreset(next);
    const range = presetRange(next);
    setFrom(range.fromUtc.slice(0, 10));
    setTo(range.toUtc.slice(0, 10));
    void load(range);
  };

  const applyCustom = () => {
    if (!from || !to) return;
    setPreset("custom");
    void load({ fromUtc: new Date(`${from}T00:00:00.000Z`).toISOString(), toUtc: new Date(`${to}T23:59:59.999Z`).toISOString() });
  };

  return <div className="account-page admin-operations-page">
    <div className="detail-header usage-header">
      <div>
        <Link className="back-link" href="/account"><ArrowLeft size={14} /> Back to account</Link>
        <p className="section-eyebrow">Internal operations</p>
        <h1>Platform operations</h1>
        <p>Read-only operational data for active Taslim administrators.</p>
      </div>
      <div className="detail-icon"><ShieldCheck size={21} /></div>
    </div>

    <div className="usage-notice admin-usage-notice"><Activity size={16} /><span>Indicators below are derived from recorded jobs, usage, billing, and storage data. This view does not infer uptime or provider health.</span></div>

    <div className="admin-range-controls">
      <div className="admin-range-presets">
        <button className={preset === "today" ? "secondary-button is-active" : "secondary-button"} onClick={() => applyPreset("today")}>Today</button>
        <button className={preset === "sevenDays" ? "secondary-button is-active" : "secondary-button"} onClick={() => applyPreset("sevenDays")}>7 days</button>
        <button className={preset === "thirtyDays" ? "secondary-button is-active" : "secondary-button"} onClick={() => applyPreset("thirtyDays")}>30 days</button>
      </div>
      <div className="admin-custom-range">
        <label>From<input type="date" value={from} onChange={(event) => { setPreset("custom"); setFrom(event.target.value); }} /></label>
        <label>To<input type="date" value={to} onChange={(event) => { setPreset("custom"); setTo(event.target.value); }} /></label>
        <button className="primary-button" onClick={applyCustom}><RefreshCw size={15} /> Apply</button>
      </div>
    </div>

    {loading && <div className="account-card usage-loading">Loading recorded operations data…</div>}
    {error && <div className="form-error">{error}</div>}
    {!loading && !error && dashboard && <>
      <div className="usage-stat-grid admin-kpi-grid operations-kpi-grid">
        <div className="account-card usage-stat"><span><Workflow size={15} /> Jobs in range</span><strong>{dashboard.generation.totalJobsInRange.toLocaleString()}</strong></div>
        <div className="account-card usage-stat"><span><Gauge size={15} /> Usage requests</span><strong>{dashboard.usage.requestCount.toLocaleString()}</strong></div>
        <div className="account-card usage-stat"><span><CircleDollarSign size={15} /> Provider cost</span><strong>{money(dashboard.usage.providerCostUsd)}</strong></div>
        <div className="account-card usage-stat"><span><CircleDollarSign size={15} /> Customer charges</span><strong>{money(dashboard.usage.customerChargesUsd)}</strong></div>
      </div>

      <section className="operations-section" aria-labelledby="operations-signals">
        <div className="operations-section-heading"><div><p className="section-eyebrow">Recorded signals</p><h2 id="operations-signals">Operational signals</h2></div><span>Live database state</span></div>
        <div className="operations-signal-grid">
          <div className="account-card operations-signal"><span>Running jobs</span><strong>{dashboard.signals.runningJobCount}</strong></div>
          <div className="account-card operations-signal"><span>Queued or pending</span><strong>{dashboard.signals.queuedOrPendingJobCount}</strong></div>
          <div className="account-card operations-signal"><span>Failures in range</span><strong>{dashboard.signals.recentFailureCount}</strong></div>
          <div className="account-card operations-signal"><span>Usage anomalies in range</span><strong>{dashboard.signals.anomalousUsageCountInRange}</strong></div>
          <div className="account-card operations-signal operations-signal-wide"><span>Last completed generation</span><strong>{dateTime(dashboard.signals.lastCompletedGenerationAt)}</strong></div>
        </div>
      </section>

      <section className="operations-section" aria-labelledby="operations-generation">
        <div className="operations-section-heading"><div><p className="section-eyebrow">Generation</p><h2 id="operations-generation">Job execution</h2></div><span>{dashboard.generation.queuedOrPendingCount} currently queued or pending</span></div>
        <div className="operations-two-column">
          <div className="account-card operations-card"><h3>Jobs by status</h3><CountList items={dashboard.generation.byStatus} /></div>
          <div className="account-card operations-card"><h3>Feature / studio breakdown</h3><CountList items={dashboard.generation.byStudio} /></div>
        </div>
        <div className="operations-two-column">
          <div className="account-card operations-card"><h3>Running jobs</h3>{dashboard.generation.runningJobs.length ? <div className="operations-detail-list">{dashboard.generation.runningJobs.map((job) => <div key={job.jobId}><span><strong>{job.jobType}</strong><small>Started {dateTime(job.startedAt ?? job.queuedAt ?? job.createdAt)}</small></span><b>{job.progressPercent}%</b></div>)}</div> : <p className="operations-empty">No jobs are currently running.</p>}</div>
          <div className="account-card operations-card"><h3>Recent failures</h3>{dashboard.generation.recentFailures.length ? <div className="operations-detail-list">{dashboard.generation.recentFailures.map((job) => <div key={job.jobId}><span><strong>{job.jobType}</strong><small>{job.errorCode ?? "No error code recorded"} · {dateTime(job.failedAt)}</small></span></div>)}</div> : <p className="operations-empty">No failed jobs recorded.</p>}</div>
        </div>
      </section>

      <section className="operations-section" aria-labelledby="operations-usage">
        <div className="operations-section-heading"><div><p className="section-eyebrow">Usage & cost</p><h2 id="operations-usage">Request and unit accounting</h2></div><span>Provider cost and customer charges are shown separately</span></div>
        <div className="operations-two-column">
          <div className="account-card operations-card"><h3>Recorded units</h3><dl className="operations-metric-grid"><div><dt>Input tokens</dt><dd>{dashboard.usage.inputTokens.toLocaleString()}</dd></div><div><dt>Cached input</dt><dd>{dashboard.usage.cachedInputTokens.toLocaleString()}</dd></div><div><dt>Output tokens</dt><dd>{dashboard.usage.outputTokens.toLocaleString()}</dd></div><div><dt>Image input units</dt><dd>{dashboard.usage.imageInputTokens.toLocaleString()}</dd></div><div><dt>Image output units</dt><dd>{dashboard.usage.imageOutputTokens.toLocaleString()}</dd></div><div><dt>Pending estimated cost</dt><dd>{money(dashboard.usage.pendingEstimatedProviderCostUsd)}</dd></div></dl></div>
          <div className="account-card operations-card"><h3>Request outcomes</h3><dl className="operations-metric-grid"><div><dt>Completed</dt><dd>{dashboard.usage.completedRequestCount.toLocaleString()}</dd></div><div><dt>Failed</dt><dd>{dashboard.usage.failedRequestCount.toLocaleString()}</dd></div><div><dt>Pending</dt><dd>{dashboard.usage.pendingRequestCount.toLocaleString()}</dd></div><div><dt>Provider cost</dt><dd>{money(dashboard.usage.providerCostUsd)}</dd></div><div><dt>Customer charges</dt><dd>{money(dashboard.usage.customerChargesUsd)}</dd></div></dl></div>
        </div>
        <div className="account-card operations-card"><h3>Usage by feature</h3>{dashboard.usage.byFeature.length ? <div className="operations-table-wrap"><table className="operations-table"><thead><tr><th>Feature</th><th>Requests</th><th>Tokens / units</th><th>Provider cost</th><th>Customer charges</th></tr></thead><tbody>{dashboard.usage.byFeature.map((feature) => <tr key={feature.feature}><td>{feature.feature}</td><td>{feature.requestCount.toLocaleString()} <small>{feature.failedRequestCount} failed</small></td><td>{(feature.inputTokens + feature.cachedInputTokens + feature.outputTokens + feature.imageInputTokens + feature.imageOutputTokens).toLocaleString()}</td><td>{money(feature.providerCostUsd)}</td><td>{money(feature.customerChargesUsd)}</td></tr>)}</tbody></table></div> : <p className="operations-empty">No usage transactions in the selected range.</p>}</div>
      </section>

      <section className="operations-section" aria-labelledby="operations-platform">
        <div className="operations-section-heading"><div><p className="section-eyebrow">Platform inventory</p><h2 id="operations-platform">Users, workspaces, assets & storage</h2></div><span>Aggregate counts only</span></div>
        <div className="operations-three-column">
          <div className="account-card operations-card"><UsersRound size={18} /><h3>Users & workspaces</h3><dl className="operations-metric-grid"><div><dt>Users</dt><dd>{dashboard.usersAndWorkspaces.totalUsers}</dd></div><div><dt>Active users</dt><dd>{dashboard.usersAndWorkspaces.activeUsers}</dd></div><div><dt>Disabled users</dt><dd>{dashboard.usersAndWorkspaces.disabledUsers}</dd></div><div><dt>Workspaces</dt><dd>{dashboard.usersAndWorkspaces.totalWorkspaces}</dd></div><div><dt>Business workspaces</dt><dd>{dashboard.usersAndWorkspaces.businessWorkspaces}</dd></div><div><dt>Archived workspaces</dt><dd>{dashboard.usersAndWorkspaces.archivedWorkspaces}</dd></div></dl></div>
          <div className="account-card operations-card"><Boxes size={18} /><h3>Assets</h3><p className="operations-big-number">{dashboard.assetsAndStorage.totalAssets.toLocaleString()}</p><span className="operations-caption">Total assets</span><CountList items={dashboard.assetsAndStorage.assetsByType} empty="No assets created in this range." /></div>
          <div className="account-card operations-card"><Database size={18} /><h3>Storage</h3><dl className="operations-metric-grid"><div><dt>Stored files</dt><dd>{dashboard.assetsAndStorage.totalStoredFiles.toLocaleString()}</dd></div><div><dt>Recorded bytes</dt><dd>{bytes(dashboard.assetsAndStorage.storedBytes)}</dd></div><div><dt>Configured provider</dt><dd>{dashboard.assetsAndStorage.configuredStorageProvider}</dd></div><div><dt>Persistent storage configured</dt><dd>{dashboard.assetsAndStorage.persistentStorageConfigured ? "Yes" : "No"}</dd></div></dl><CountList items={dashboard.assetsAndStorage.filesByStatus} empty="No stored files." /></div>
        </div>
      </section>

      <section className="operations-section" aria-labelledby="operations-billing">
        <div className="operations-section-heading"><div><p className="section-eyebrow">Billing operations</p><h2 id="operations-billing">Read-only billing state</h2></div><span>No charging controls in this dashboard</span></div>
        <div className="operations-three-column">
          <div className="account-card operations-card"><CircleDollarSign size={18} /><h3>Charging state</h3><dl className="operations-metric-grid"><div><dt>Customer charging</dt><dd>{dashboard.billing.customerChargingEnabled ? "Enabled" : "Disabled"}</dd></div><div><dt>Configured provider</dt><dd>{dashboard.billing.configuredProvider}</dd></div><div><dt>Provider configured</dt><dd>{dashboard.billing.paymentProviderConfigured ? "Yes" : "No"}</dd></div><div><dt>Pending reconciliation</dt><dd>{dashboard.billing.pendingReconciliationCount}</dd></div></dl></div>
          <div className="account-card operations-card"><h3>Subscriptions by plan / status</h3>{dashboard.billing.subscriptions.length ? <div className="operations-detail-list">{dashboard.billing.subscriptions.map((item) => <div key={`${item.planCode}-${item.status}`}><span><strong>{item.planCode}</strong><small>{item.status}</small></span><b>{item.count}</b></div>)}</div> : <p className="operations-empty">No subscriptions recorded.</p>}</div>
          <div className="account-card operations-card"><FileStack size={18} /><h3>Payment attempts</h3><CountList items={dashboard.billing.paymentAttemptsByStatus} empty="No payment attempts recorded." /></div>
        </div>
      </section>
    </>}
  </div>;
}
