"use client";
import { localeTag } from "@/lib/i18n";

import Link from "next/link";
import { ArrowLeft, CalendarClock, CheckCircle2, Coins, CreditCard, History, Sparkles } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type BillingAccount, type BillingPlanOption } from "@/lib/api";
import { describeCreditBalance, scopedBillingAccount } from "@/lib/billingCreditBalance";

export function BillingView() {
  const { user, workspace, loading } = useAuth();
  // Remount the private billing state on every principal/workspace transition.
  // Render-time scope comparisons already prevent A's data showing to B, but
  // without a remount an A → B → A switch can show A's *old* ledger/balance
  // while A's fresh request is still in flight. A credit page must not imply
  // stale balances are current.
  if (loading || !user?.id || !workspace?.id) return null;
  return <ScopedBillingView key={`${user.id}:${workspace.id}`} userId={user.id} workspaceId={workspace.id} />;
}

function ScopedBillingView({ userId, workspaceId }: { userId: string; workspaceId: string }) {
  const { t, locale } = useLocale();
  const [snapshot, setSnapshot] = useState<{ userId: string; workspaceId: string; account: BillingAccount } | null>(null);
  const [failedScope, setFailedScope] = useState<{ userId: string; workspaceId: string } | null>(null);
  // Resolve against the *current* auth/workspace identity during render. Waiting
  // for an effect to clear previous state can briefly expose the old workspace's
  // private credit ledger and transaction reasons after a same-tab switch.
  const error = Boolean(userId && workspaceId && failedScope?.userId === userId && failedScope?.workspaceId === workspaceId);
  // On any failed current-scope refresh never leave a cached ledger visible
  // beside the error, even if the snapshot came from the same identity.
  const account = error ? null : scopedBillingAccount(snapshot, userId, workspaceId);
  useEffect(() => {
    if (!userId || !workspaceId) return;
    let active = true;
    void api.getBillingAccount(workspaceId).then((next) => {
      if (!active) return;
      setSnapshot({ userId, workspaceId, account: next });
      setFailedScope(null);
    }).catch(() => {
      if (active) setFailedScope({ userId, workspaceId });
    });
    return () => { active = false; };
  }, [userId, workspaceId]);
  const dateFormatter = useMemo(() => new Intl.DateTimeFormat(localeTag(locale), { dateStyle: "medium" }), [locale]);
  const numberFormatter = useMemo(() => new Intl.NumberFormat(localeTag(locale)), [locale]);
  // Only the API's totalRemaining is a spendable balance; other figures
  // describe issued or allocated credit and must not be presented as usage.
  const balance = useMemo(() => describeCreditBalance(account?.credits), [account]);
  const formatDate = (value: string) => dateFormatter.format(new Date(value));
  const formatCredits = (value: number) => numberFormatter.format(value);
  const formatPrice = (plan: Pick<BillingPlanOption, "monthlyPriceUsd">) => plan.monthlyPriceUsd === 0 ? t("billing.noCharge") : `$${plan.monthlyPriceUsd} / ${t("billing.month")}`;
  const entryLabel = (type: string) => t(`billing.entry.${type.toLowerCase()}`);
  const paymentStatusLabel = (status: string) => t(`billing.paymentStatus.${status}`);

  // The billing action buttons have no mutation handlers yet. API availability
  // flags are not a substitute for wired, CSRF-protected checkout/cancel flows;
  // keep these visible placeholders disabled even if server policy changes.
  return <div className="account-page billing-page">
    <div className="detail-header usage-header"><div><Link className="back-link" href="/account"><ArrowLeft size={14} /> {t("navigation.account")}</Link><p className="section-eyebrow">{t("billing.eyebrow")}</p><h1>{t("billing.title")}</h1><p>{t("billing.subtitle")}</p></div><div className="detail-icon"><CreditCard size={21} /></div></div>
    <div className="billing-notice"><CheckCircle2 size={16} /><span>{t("billing.transparentNotice")}</span></div>
    {error && <div className="form-error">{t("billing.loadError")}</div>}
    {!account && !error && <div className="account-card usage-loading">{t("billing.loading")}</div>}
    {account && <>
      <div className="billing-summary-grid">
        <div className="account-card billing-plan-card"><div className="card-title"><span className="card-title-icon"><Sparkles size={17} /></span><div><h2>{t("billing.currentPlan")}</h2><p>{t("billing.planStatus", { status: account.subscription.status })}</p></div></div><strong>{account.currentPlan.name}</strong><span>{formatPrice(account.currentPlan)}</span></div>
        <div className="account-card billing-credit-card"><div className="card-title"><span className="card-title-icon teal"><Coins size={17} /></span><div><h2>{t("billing.remainingCredits")}</h2><p>{t("billing.creditUnit")}</p></div></div><strong>{formatCredits(balance.totalRemaining)}</strong><span>{t("billing.includedCredits", { count: formatCredits(balance.includedAllocation) })}</span></div>
        <div className="account-card billing-period-card"><div className="card-title"><span className="card-title-icon"><CalendarClock size={17} /></span><div><h2>{t("billing.billingPeriod")}</h2><p>{t("billing.periodStatus", { status: account.billingPeriod.status })}</p></div></div><strong>{formatDate(account.billingPeriod.startsAt)} – {formatDate(account.billingPeriod.endsAt)}</strong><span>{t("billing.renewal", { date: formatDate(account.subscription.nextRenewalAt) })}</span></div>
      </div>
      <div className="account-card billing-payment-status"><div className="card-title"><span className="card-title-icon"><CreditCard size={17} /></span><div><h2>{t("billing.paymentStatusTitle")}</h2><p>{t("billing.paymentStatusSubtitle")}</p></div></div><strong>{paymentStatusLabel(account.paymentStatus.status)}</strong>{account.paymentStatus.lastFailureReason && <p className="form-error">{account.paymentStatus.lastFailureReason}</p>}<small>{account.actions.disabledReason}</small></div>
      <div className="account-card billing-plans"><div className="card-title"><span className="card-title-icon teal"><Sparkles size={17} /></span><div><h2>{t("billing.plansTitle")}</h2><p>{t("billing.plansSubtitle")}</p></div></div><div className="billing-plan-options">{account.availablePlans.map((plan) => <div className={`billing-plan-option${plan.isCurrent ? " current" : ""}`} key={plan.code}><div><strong>{plan.name}</strong>{plan.isCurrent && <span className="billing-current-badge">{t("billing.currentBadge")}</span>}</div><b>{formatPrice(plan)}</b><span>{t("billing.includedCredits", { count: formatCredits(plan.monthlyCreditAllowance) })}</span><button className="secondary-button" type="button" disabled>{plan.isCurrent ? t("billing.currentPlan") : plan.monthlyPriceUsd > account.currentPlan.monthlyPriceUsd ? t("billing.upgradePlaceholder") : t("billing.downgradePlaceholder")}</button></div>)}</div></div>
      <div className="account-grid billing-grid">
        <div className="account-card"><div className="card-title"><span className="card-title-icon teal"><Sparkles size={17} /></span><div><h2>{t("billing.changeTitle")}</h2><p>{t("billing.changeSubtitle")}</p></div></div><div className="button-row"><button className="secondary-button" type="button" disabled>{t("billing.upgradePlaceholder")}</button><button className="secondary-button" type="button" disabled>{t("billing.downgradePlaceholder")}</button></div></div>
        <div className="account-card"><div className="card-title"><span className="card-title-icon"><CalendarClock size={17} /></span><div><h2>{t("billing.cancelTitle")}</h2><p>{account.subscription.cancelAtPeriodEnd ? t("billing.cancelScheduled") : t("billing.cancelSubtitle")}</p></div></div><button className="secondary-button" type="button" disabled>{t("billing.cancelPlaceholder")}</button></div>
      </div>
      <div className="account-card"><div className="card-title"><span className="card-title-icon"><Coins size={17} /></span><div><h2>{t("billing.allowanceTitle")}</h2><p>{t("billing.allowanceSubtitle")}</p></div></div><dl className="billing-definition-list"><div><dt>{t("billing.includedAllowance")}</dt><dd>{formatCredits(account.credits.includedGranted)}</dd></div><div><dt>{t("billing.purchasedCredits")}</dt><dd>{formatCredits(account.credits.purchasedRemaining)}</dd></div><div><dt>{t("billing.corrections")}</dt><dd>{formatCredits(account.credits.adjustmentBalance)}</dd></div><div><dt>{t("billing.remainingCredits")}</dt><dd>{formatCredits(balance.totalRemaining)}</dd></div></dl></div>
      <div className="account-card billing-history"><div className="card-title"><span className="card-title-icon"><History size={17} /></span><div><h2>{t("billing.historyTitle")}</h2><p>{t("billing.historySubtitle")}</p></div></div>{account.transactions.length ? <div className="usage-table-wrap"><table className="usage-table billing-table"><thead><tr><th>{t("billing.transactionType")}</th><th>{t("billing.transactionReason")}</th><th>{t("billing.transactionAmount")}</th><th>{t("billing.transactionDate")}</th></tr></thead><tbody>{account.transactions.map((entry) => <tr key={entry.id}><td><strong>{entryLabel(entry.type)}</strong></td><td>{entry.reason}</td><td className={entry.amount < 0 ? "billing-negative" : "billing-positive"}>{entry.amount > 0 ? "+" : ""}{formatCredits(entry.amount)}</td><td>{formatDate(entry.createdAt)}</td></tr>)}</tbody></table></div> : <p className="usage-empty">{t("billing.noTransactions")}</p>}</div>
    </>}
  </div>;
}
