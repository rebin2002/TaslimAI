"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { ArrowLeft, BarChart3, CheckCircle2, CircleAlert, Coins, Hash, ReceiptText } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type UsageHistory, type UsageSummary, type UsageTransaction } from "@/lib/api";

function statusKey(status: UsageTransaction["status"]) {
  return status === "Completed" ? "usage.completed" : status === "Failed" ? "usage.failed" : status === "Refunded" ? "usage.refunded" : "usage.pending";
}

export function UsageView() {
  const { workspace } = useAuth();
  const { locale, t } = useLocale();
  const [summary, setSummary] = useState<UsageSummary | null>(null);
  const [history, setHistory] = useState<UsageHistory | null>(null);
  const [page, setPage] = useState(1);
  const [loadedWorkspaceId, setLoadedWorkspaceId] = useState<string | null>(null);
  const [error, setError] = useState(false);
  const numberFormat = useMemo(() => new Intl.NumberFormat(locale), [locale]);

  useEffect(() => {
    if (!workspace?.id) return;
    let active = true;
    void Promise.all([api.getUsageSummary(workspace.id), api.getUsageHistory(workspace.id, page)])
      .then(([nextSummary, nextHistory]) => {
        if (!active) return;
        setSummary(nextSummary);
        setHistory(nextHistory);
        setLoadedWorkspaceId(workspace.id);
      })
      .catch(() => {
        if (active) setError(true);
      })
    return () => { active = false; };
  }, [page, workspace?.id]);

  const loading = !error && (!workspace?.id || loadedWorkspaceId !== workspace.id || !history || history.page !== page);
  const formatNumber = (value: number) => numberFormat.format(value);
  const formatUsd = (value: number) => `${value.toFixed(8)} ${t("usage.usd")}`;
  const featureLabel = (feature: string) => feature === "Chat" ? t("usage.featureChat") : feature;

  return <div className="account-page usage-page">
    <div className="detail-header usage-header"><div><Link className="back-link" href="/account"><ArrowLeft size={14} /> {t("navigation.account")}</Link><p className="section-eyebrow">{t("usage.eyebrow")}</p><h1>{t("usage.title")}</h1><p>{t("usage.subtitle")}</p></div><div className="detail-icon"><BarChart3 size={21} /></div></div>
    <div className="usage-notice"><ReceiptText size={16} /><span>{t("usage.internalNotice")}</span></div>
    {loading && <div className="account-card usage-loading">{t("usage.loading")}</div>}
    {error && <div className="form-error">{t("usage.loadError")}</div>}
    {!loading && !error && summary && <>
      <div className="usage-stat-grid">
        <div className="account-card usage-stat"><span><Hash size={15} /> {t("usage.totalRequests")}</span><strong>{formatNumber(summary.totalRequests)}</strong></div>
        <div className="account-card usage-stat"><span><CheckCircle2 size={15} /> {t("usage.completedRequests")}</span><strong>{formatNumber(summary.completedRequests)}</strong></div>
        <div className="account-card usage-stat"><span><CircleAlert size={15} /> {t("usage.failedRequests")}</span><strong>{formatNumber(summary.failedRequests)}</strong></div>
        <div className="account-card usage-stat"><span><Coins size={15} /> {t("usage.customerCharge")}</span><strong>{formatUsd(summary.customerChargedAmount)}</strong></div>
      </div>
      <div className="account-grid usage-grid">
        <div className="account-card"><div className="card-title"><span className="card-title-icon"><BarChart3 size={17} /></span><div><h2>{t("usage.tokens")}</h2><p>{t("usage.subtitle")}</p></div></div><dl className="usage-definition-list"><div><dt>{t("usage.inputTokens")}</dt><dd>{formatNumber(summary.inputTokens)}</dd></div><div><dt>{t("usage.cachedInputTokens")}</dt><dd>{formatNumber(summary.cachedInputTokens)}</dd></div><div><dt>{t("usage.outputTokens")}</dt><dd>{formatNumber(summary.outputTokens)}</dd></div></dl></div>
        <div className="account-card"><div className="card-title"><span className="card-title-icon teal"><Coins size={17} /></span><div><h2>{t("usage.providerCost")}</h2><p>{t("usage.internalNotice")}</p></div></div><strong className="usage-cost">{formatUsd(summary.providerCostUsd)}</strong></div>
      </div>
      <div className="account-card usage-history"><div className="card-title"><span className="card-title-icon"><ReceiptText size={17} /></span><div><h2>{t("usage.history")}</h2><p>{t("usage.subtitle")}</p></div></div>{history?.items.length ? <div className="usage-table-wrap"><table className="usage-table"><thead><tr><th>{t("usage.featureChat")}</th><th>{t("usage.totalTokens")}</th><th>{t("usage.providerCost")}</th><th>{t("usage.customerCharge")}</th></tr></thead><tbody>{history.items.map((item) => <tr key={item.id}><td><strong>{featureLabel(item.feature)}</strong><small className={`usage-status is-${item.status.toLowerCase()}`}>{t(statusKey(item.status))}</small></td><td>{item.inputTokens === null && item.outputTokens === null ? "—" : formatNumber((item.inputTokens ?? 0) + (item.outputTokens ?? 0))}</td><td>{formatUsd(item.providerCostUsd)}</td><td>{formatUsd(item.chargedAmount)}</td></tr>)}</tbody></table></div> : <p className="usage-empty">{t("usage.noUsage")}</p>}{history && history.totalPages > 1 && <div className="usage-pagination"><button className="secondary-button" disabled={history.page <= 1} onClick={() => { setError(false); setPage((current) => Math.max(1, current - 1)); }}>{t("usage.previous")}</button><span>{t("usage.page", { page: String(history.page), total: String(history.totalPages) })}</span><button className="secondary-button" disabled={history.page >= history.totalPages} onClick={() => { setError(false); setPage((current) => current + 1); }}>{t("usage.next")}</button></div>}</div>
    </>}
  </div>;
}
