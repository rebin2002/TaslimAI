"use client";

import Link from "next/link";
import { useCallback, useEffect, useMemo, useState } from "react";
import { ArrowLeft, BarChart3, CircleAlert, Coins, RefreshCw, Search, ShieldCheck } from "lucide-react";
import { useLocale } from "@/components/LocaleProvider";
import { api, type AdminUsageReport, type AdminUsageTransaction } from "@/lib/api";
import { dailyCostMaximum, presetRange } from "@/lib/adminUsageState";

function money(value: number, currency: string) {
  return `${value.toFixed(8)} ${currency}`;
}

function dateInputValue(value: Date) {
  return value.toISOString().slice(0, 10);
}

function readableJson(value: string | null) {
  if (!value) return null;
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

function costBasisLabel(value: string | null, t: (key: string) => string) {
  return value === "Actual" ? t("admin.actual") : value === "Estimated" ? t("admin.estimated") : t("admin.noValue");
}

export function AdminUsageView() {
  const { t, locale } = useLocale();
  const [report, setReport] = useState<AdminUsageReport | null>(null);
  const [selected, setSelected] = useState<AdminUsageTransaction | null>(null);
  const [preset, setPreset] = useState<"today" | "sevenDays" | "thirtyDays" | "custom">("thirtyDays");
  const [from, setFrom] = useState(() => dateInputValue(new Date(Date.now() - 29 * 86_400_000)));
  const [to, setTo] = useState(() => dateInputValue(new Date()));
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  const load = useCallback(async (range: { fromUtc: string; toUtc: string }) => {
    setLoading(true);
    setError(false);
    try {
      const next = await api.getAdminUsageReport({ fromUtc: range.fromUtc, toUtc: range.toUtc, page: 1, pageSize: 20 });
      setReport(next);
      setSelected(null);
    } catch {
      setError(true);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const range = presetRange("thirtyDays");
    // Initial remote report load intentionally updates dashboard state.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void load(range);
  }, [load]);

  const applyPreset = (next: "today" | "sevenDays" | "thirtyDays") => {
    setPreset(next);
    const range = presetRange(next);
    setFrom(range.fromUtc.slice(0, 10));
    setTo(range.toUtc.slice(0, 10));
    void load(range);
  };

  const applyCustom = () => {
    const fromDate = new Date(`${from}T00:00:00.000Z`);
    const toDate = new Date(`${to}T23:59:59.999Z`);
    void load({ fromUtc: fromDate.toISOString(), toUtc: toDate.toISOString() });
  };

  const maximum = useMemo(() => dailyCostMaximum(report?.breakdowns.byDay ?? []), [report]);
  const formatDate = (value: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(value));
  const formatDateTime = (value: string) => new Intl.DateTimeFormat(locale, { dateStyle: "short", timeStyle: "short" }).format(new Date(value));
  const snapshot = readableJson(selected?.pricingSnapshotJson ?? null);

  return <div className="account-page admin-usage-page">
    <div className="detail-header usage-header"><div><Link className="back-link" href="/account"><ArrowLeft size={14} /> {t("admin.backAccount")}</Link><p className="section-eyebrow">{t("admin.eyebrow")}</p><h1>{t("admin.usageTitle")}</h1><p>{t("admin.usageSubtitle")}</p></div><div className="detail-icon"><ShieldCheck size={21} /></div></div>
    <div className="usage-notice admin-usage-notice"><ShieldCheck size={16} /><span>{t("admin.usageSubtitle")}</span></div>
    <div className="admin-range-controls">
      <div className="admin-range-presets"><button className={preset === "today" ? "secondary-button is-active" : "secondary-button"} onClick={() => applyPreset("today")}>{t("admin.today")}</button><button className={preset === "sevenDays" ? "secondary-button is-active" : "secondary-button"} onClick={() => applyPreset("sevenDays")}>{t("admin.sevenDays")}</button><button className={preset === "thirtyDays" ? "secondary-button is-active" : "secondary-button"} onClick={() => applyPreset("thirtyDays")}>{t("admin.thirtyDays")}</button></div>
      <div className="admin-custom-range"><label>{t("admin.from")}<input type="date" value={from} onChange={(event) => { setPreset("custom"); setFrom(event.target.value); }} /></label><label>{t("admin.to")}<input type="date" value={to} onChange={(event) => { setPreset("custom"); setTo(event.target.value); }} /></label><button className="primary-button" onClick={applyCustom}><RefreshCw size={15} /> {t("admin.apply")}</button></div>
    </div>
    {loading && <div className="account-card usage-loading">{t("admin.loading")}</div>}
    {error && <div className="form-error">{t("admin.loadError")}</div>}
    {!loading && !error && report && <>
      <div className="usage-stat-grid admin-kpi-grid"><div className="account-card usage-stat"><span><Search size={15} /> {t("admin.transactions")}</span><strong>{report.summary.transactionCount}</strong></div><div className="account-card usage-stat"><span><Coins size={15} /> {t("admin.providerCost")}</span><strong>{money(report.summary.totalProviderCostUsd, report.summary.currency)}</strong></div><div className="account-card usage-stat"><span><Coins size={15} /> {t("admin.customerCharges")}</span><strong>{money(report.summary.totalCustomerChargesUsd, report.summary.currency)}</strong></div><div className="account-card usage-stat"><span><CircleAlert size={15} /> {t("admin.anomaly")}</span><strong>{report.summary.anomalousCount}</strong></div></div>
      <div className="account-grid admin-usage-grid">
        <div className="account-card admin-trend-card"><div className="card-title"><span className="card-title-icon"><BarChart3 size={17} /></span><div><h2>{t("admin.dailyTrend")}</h2><p>{formatDate(report.summary.fromUtc)} – {formatDate(report.summary.toUtc)}</p></div></div><div className="admin-trend-bars">{report.breakdowns.byDay.length ? report.breakdowns.byDay.map((day) => <div className="admin-trend-day" key={day.dayUtc} title={`${formatDate(day.dayUtc)}: ${money(day.providerCostUsd, report.summary.currency)}`}><div className="admin-trend-bar" style={{ height: `${Math.max(4, maximum ? (day.providerCostUsd / maximum) * 100 : 4)}%` }} /><small>{new Date(day.dayUtc).getUTCDate()}</small></div>) : <p className="usage-empty">{t("admin.empty")}</p>}</div></div>
        <div className="account-card"><div className="card-title"><span className="card-title-icon teal"><BarChart3 size={17} /></span><div><h2>{t("admin.byFeature")}</h2><p>{t("admin.providerCost")}</p></div></div><div className="admin-breakdown-list">{report.breakdowns.byFeature.map((item) => <div key={item.feature}><span>{item.feature}</span><strong>{money(item.providerCostUsd, report.summary.currency)}</strong></div>)}</div></div>
      </div>
      <div className="account-grid admin-usage-grid"><div className="account-card"><div className="card-title"><div><h2>{t("admin.byWorkspace")}</h2></div></div><div className="admin-breakdown-list">{report.breakdowns.byWorkspace.map((item) => <div key={item.workspaceId}><span>{item.workspaceName}</span><strong>{money(item.providerCostUsd, report.summary.currency)}</strong></div>)}</div></div><div className="account-card"><div className="card-title"><div><h2>{t("admin.byUser")}</h2></div></div><div className="admin-breakdown-list">{report.breakdowns.byUser.map((item) => <div key={item.userId}><span>{item.displayName || item.email || t("admin.unknown")}</span><strong>{money(item.providerCostUsd, report.summary.currency)}</strong></div>)}</div></div></div>
      <div className="account-card admin-transactions-card"><div className="card-title"><span className="card-title-icon"><Search size={17} /></span><div><h2>{t("admin.recent")}</h2><p>{report.transactions.totalCount} · {t("admin.inspect")}</p></div></div>{report.transactions.items.length ? <div className="usage-table-wrap"><table className="usage-table admin-usage-table"><thead><tr><th>{t("admin.timestamp")}</th><th>{t("admin.feature")}</th><th>{t("admin.workspace")}</th><th>{t("admin.providerCost")}</th><th>{t("admin.status")}</th><th /></tr></thead><tbody>{report.transactions.items.map((item) => <tr key={item.id}><td>{formatDateTime(item.createdAt)}</td><td>{item.feature}</td><td>{item.workspaceName}</td><td>{money(item.providerCostUsd, item.currency)}</td><td><small className={`usage-status is-${item.status.toLowerCase()}`}>{item.status}</small></td><td><button className="text-button" onClick={() => { setSelected(item); void api.getAdminUsageTransaction(item.id).then(setSelected).catch(() => undefined); }}>{t("admin.inspect")}</button></td></tr>)}</tbody></table></div> : <p className="usage-empty">{t("admin.empty")}</p>}</div>
      {selected && <div className="account-card admin-inspector"><div className="card-title"><span className="card-title-icon"><ShieldCheck size={17} /></span><div><h2>{t("admin.transaction")}</h2><p>{selected.id}</p></div></div><dl className="admin-inspector-grid"><div><dt>{t("admin.provider")}</dt><dd>{selected.provider || t("admin.noValue")}</dd></div><div><dt>{t("admin.model")}</dt><dd>{selected.model || t("admin.noValue")}</dd></div><div><dt>{t("admin.providerCostExact")}</dt><dd>{money(selected.providerCostUsd, selected.currency)}</dd></div><div><dt>{t("admin.estimatedCost")}</dt><dd>{selected.estimatedProviderCostUsd === null ? t("admin.noValue") : money(selected.estimatedProviderCostUsd, selected.currency)}</dd></div><div><dt>{t("admin.customerCharge")}</dt><dd>{money(selected.chargedAmount, selected.currency)}</dd></div><div><dt>{t("admin.currency")}</dt><dd>{selected.currency}</dd></div><div><dt>{t("admin.costBasis")}</dt><dd>{costBasisLabel(selected.costBasis, t)}</dd></div><div><dt>{t("admin.user")}</dt><dd>{selected.userDisplayName || selected.userEmail || t("admin.unknown")}</dd></div><div><dt>{t("admin.workspace")}</dt><dd>{selected.workspaceName}</dd></div><div><dt>{t("admin.generationJob")}</dt><dd>{selected.generationJobId ?? t("admin.noValue")}</dd></div><div><dt>{t("admin.inputTokens")}</dt><dd>{selected.inputTokens ?? t("admin.noValue")}</dd></div><div><dt>{t("admin.outputTokens")}</dt><dd>{selected.outputTokens ?? t("admin.noValue")}</dd></div><div><dt>{t("admin.imageInputTokens")}</dt><dd>{selected.imageInputTokens ?? t("admin.noValue")}</dd></div><div><dt>{t("admin.imageOutputTokens")}</dt><dd>{selected.imageOutputTokens ?? t("admin.noValue")}</dd></div><div><dt>{t("admin.latency")}</dt><dd>{selected.latencyMs === null ? t("admin.noValue") : `${selected.latencyMs} ms`}</dd></div><div><dt>{t("admin.pricingVersion")}</dt><dd>{selected.pricingVersion ?? t("admin.noValue")}</dd></div><div><dt>{t("admin.status")}</dt><dd>{selected.status}</dd></div><div><dt>{t("admin.failureCode")}</dt><dd>{selected.failureCode ?? t("admin.noValue")}</dd></div><div><dt>{t("admin.anomaly")}</dt><dd>{selected.anomalyCode ?? t("admin.noValue")}</dd></div></dl>{snapshot && <><h3 className="admin-json-title">{t("admin.pricingSnapshot")}</h3><pre className="admin-json-inspector">{snapshot}</pre></>}</div>}
    </>}
  </div>;
}
