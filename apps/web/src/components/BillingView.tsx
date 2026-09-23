"use client";

import Link from "next/link";
import { ArrowLeft, CalendarClock, CheckCircle2, Coins, CreditCard, History, Sparkles } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type BillingAccount } from "@/lib/api";

export function BillingView() {
  const { workspace } = useAuth();
  const { t, locale } = useLocale();
  const [account, setAccount] = useState<BillingAccount | null>(null);
  const [error, setError] = useState(false);
  useEffect(() => {
    if (!workspace?.id) return;
    let active = true;
    void api.getBillingAccount(workspace.id).then((next) => { if (active) setAccount(next); }).catch(() => { if (active) setError(true); });
    return () => { active = false; };
  }, [workspace?.id]);
  const dateFormatter = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }), [locale]);
  const numberFormatter = useMemo(() => new Intl.NumberFormat(locale), [locale]);
  const formatDate = (value: string) => dateFormatter.format(new Date(value));
  const formatCredits = (value: number) => numberFormatter.format(value);
  const entryLabel = (type: string) => t(`billing.entry.${type.toLowerCase()}`);

  return <div className="account-page billing-page">
    <div className="detail-header usage-header"><div><Link className="back-link" href="/account"><ArrowLeft size={14} /> {t("navigation.account")}</Link><p className="section-eyebrow">{t("billing.eyebrow")}</p><h1>{t("billing.title")}</h1><p>{t("billing.subtitle")}</p></div><div className="detail-icon"><CreditCard size={21} /></div></div>
    <div className="billing-notice"><CheckCircle2 size={16} /><span>{t("billing.transparentNotice")}</span></div>
    {error && <div className="form-error">{t("billing.loadError")}</div>}
    {!account && !error && <div className="account-card usage-loading">{t("billing.loading")}</div>}
    {account && <>
      <div className="billing-summary-grid">
        <div className="account-card billing-plan-card"><div className="card-title"><span className="card-title-icon"><Sparkles size={17} /></span><div><h2>{t("billing.currentPlan")}</h2><p>{t("billing.planStatus", { status: account.subscription.status })}</p></div></div><strong>{account.currentPlan.name}</strong><span>{account.currentPlan.monthlyPriceUsd === 0 ? t("billing.noCharge") : `$${account.currentPlan.monthlyPriceUsd} / ${t("billing.month")}`}</span></div>
        <div className="account-card billing-credit-card"><div className="card-title"><span className="card-title-icon teal"><Coins size={17} /></span><div><h2>{t("billing.remainingCredits")}</h2><p>{t("billing.creditUnit")}</p></div></div><strong>{formatCredits(account.credits.totalRemaining)}</strong><span>{t("billing.includedRemaining", { count: formatCredits(account.credits.includedRemaining) })}</span></div>
        <div className="account-card billing-period-card"><div className="card-title"><span className="card-title-icon"><CalendarClock size={17} /></span><div><h2>{t("billing.billingPeriod")}</h2><p>{t("billing.periodStatus", { status: account.billingPeriod.status })}</p></div></div><strong>{formatDate(account.billingPeriod.startsAt)} – {formatDate(account.billingPeriod.endsAt)}</strong><span>{t("billing.renewal", { date: formatDate(account.subscription.nextRenewalAt) })}</span></div>
      </div>
      <div className="account-grid billing-grid">
        <div className="account-card"><div className="card-title"><span className="card-title-icon teal"><Sparkles size={17} /></span><div><h2>{t("billing.upgradeTitle")}</h2><p>{t("billing.upgradeSubtitle")}</p></div></div><button className="secondary-button" disabled>{t("billing.upgradePlaceholder")}</button></div>
        <div className="account-card"><div className="card-title"><span className="card-title-icon"><Coins size={17} /></span><div><h2>{t("billing.allowanceTitle")}</h2><p>{t("billing.allowanceSubtitle")}</p></div></div><dl className="billing-definition-list"><div><dt>{t("billing.includedAllowance")}</dt><dd>{formatCredits(account.credits.includedGranted)}</dd></div><div><dt>{t("billing.purchasedCredits")}</dt><dd>{formatCredits(account.credits.purchasedRemaining)}</dd></div><div><dt>{t("billing.corrections")}</dt><dd>{formatCredits(account.credits.adjustmentBalance)}</dd></div></dl></div>
      </div>
      <div className="account-card billing-history"><div className="card-title"><span className="card-title-icon"><History size={17} /></span><div><h2>{t("billing.historyTitle")}</h2><p>{t("billing.historySubtitle")}</p></div></div>{account.transactions.length ? <div className="usage-table-wrap"><table className="usage-table billing-table"><thead><tr><th>{t("billing.transactionType")}</th><th>{t("billing.transactionReason")}</th><th>{t("billing.transactionAmount")}</th><th>{t("billing.transactionDate")}</th></tr></thead><tbody>{account.transactions.map((entry) => <tr key={entry.id}><td><strong>{entryLabel(entry.type)}</strong></td><td>{entry.reason}</td><td className={entry.amount < 0 ? "billing-negative" : "billing-positive"}>{entry.amount > 0 ? "+" : ""}{formatCredits(entry.amount)}</td><td>{formatDate(entry.createdAt)}</td></tr>)}</tbody></table></div> : <p className="usage-empty">{t("billing.noTransactions")}</p>}</div>
    </>}
  </div>;
}
