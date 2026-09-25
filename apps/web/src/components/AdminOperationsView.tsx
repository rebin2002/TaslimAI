"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { Activity, AlertTriangle, ArrowLeft, Boxes, CheckCircle2, CircleDollarSign, Clock3, Database, FileStack, Gauge, RefreshCw, ServerCog, ShieldCheck, UsersRound, Workflow, XCircle } from "lucide-react";
import { api, type AdminCountBreakdown, type AdminOperationsDashboard, type AdminProviderHealth } from "@/lib/api";
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

function providerStatusLabel(provider: AdminProviderHealth) {
	if (provider.status === "disabled") return "Disabled";
	if (provider.status === "unconfigured") return "Unconfigured";
	if (provider.status === "recent_operational_failure") return "Recent failure";
	if (provider.status === "operational") return "Operational";
	return "Available; no recent success recorded";
}

function providerStatusIcon(provider: AdminProviderHealth) {
	if (provider.status === "operational") return <CheckCircle2 size={16} aria-hidden="true" />;
	if (provider.status === "disabled" || provider.status === "unconfigured") return <XCircle size={16} aria-hidden="true" />;
	return <AlertTriangle size={16} aria-hidden="true" />;
}

function providerCost(provider: AdminProviderHealth) {
	return `${money(provider.actualProviderCostUsd)} actual · ${money(provider.estimatedProviderCostUsd)} estimated`;
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
    const timer = window.setTimeout(() => { void load(presetRange("thirtyDays")); }, 0);
    return () => window.clearTimeout(timer);
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

    <div className="usage-notice admin-usage-notice"><Activity size={16} /><span>Indicators below are derived from recorded jobs, usage, billing, storage, and provider execution data. Secrets, prompts, and raw provider payloads are never returned.</span></div>

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

      <section className="operations-section" aria-labelledby="operations-providers">
        <div className="operations-section-heading"><div><p className="section-eyebrow">Provider control plane</p><h2 id="operations-providers">Generation provider health</h2></div><span>Read-only server configuration state</span></div>
        <div className="provider-health-grid">
          {dashboard.providers.map((provider) => <article className="account-card provider-health-card" key={provider.key}>
            <div className="provider-health-heading">
              <div><span className="provider-health-category"><ServerCog size={15} /> {provider.category}</span><h3>{provider.key}</h3></div>
              <span className={`provider-status provider-status-${provider.status}`} title={providerStatusLabel(provider)}>{providerStatusIcon(provider)}<span>{providerStatusLabel(provider)}</span></span>
            </div>
            <dl className="operations-metric-grid provider-state-grid">
              <div><dt>Enabled</dt><dd>{provider.enabled ? "Yes" : "No"}</dd></div>
              <div><dt>Configured</dt><dd>{provider.configured ? "Yes" : "No"}</dd></div>
              <div><dt>Recent success / failure</dt><dd>{provider.recentSuccessCount} / {provider.recentFailureCount}</dd></div>
              <div><dt>Average latency</dt><dd>{provider.averageLatencyMs === null ? "Not recorded" : `${provider.averageLatencyMs.toLocaleString()} ms`}</dd></div>
              <div><dt>Rate-limit events</dt><dd>{provider.rateLimitEventCount.toLocaleString()}</dd></div>
              <div><dt>Timeout events</dt><dd>{provider.timeoutEventCount.toLocaleString()}</dd></div>
              <div><dt>QC failures</dt><dd>{provider.qualityControlFailureCount.toLocaleString()}</dd></div>
              <div><dt>Retries</dt><dd>{provider.retryCount.toLocaleString()}</dd></div>
              <div><dt>Fallbacks</dt><dd>{provider.fallbackTelemetryRecorded ? provider.fallbackCount.toLocaleString() : "Not recorded"}</dd></div>
              <div><dt>Provider cost</dt><dd>{providerCost(provider)}</dd></div>
              <div><dt>Last success</dt><dd>{provider.lastSuccessAt ? dateTime(provider.lastSuccessAt) : "Not recorded"}</dd></div>
              <div><dt>Last failure</dt><dd>{provider.lastFailureAt ? `${provider.lastFailureCode ?? "Sanitized failure"} · ${dateTime(provider.lastFailureAt)}` : "Not recorded"}</dd></div>
            </dl>
            <div className="provider-health-failures"><h4><Clock3 size={14} /> Recent sanitized failures</h4>{provider.recentFailures.length ? <div className="operations-detail-list">{provider.recentFailures.map((failure, index) => <div key={`${failure.occurredAt}-${failure.errorCode}-${index}`}><span><strong>{failure.errorCode}</strong><small>{failure.jobType} · {dateTime(failure.occurredAt)}</small></span></div>)}</div> : <p className="operations-empty">No sanitized provider failures recorded.</p>}</div>
          </article>)}
        </div>
        <p className="provider-health-footnote">Provider activation remains controlled by secure server configuration. Fallback telemetry is not persisted by the current architecture, so it is shown as not recorded rather than inferred.</p>
      </section>

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
