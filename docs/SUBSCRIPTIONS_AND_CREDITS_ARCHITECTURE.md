# Subscriptions and Credits Architecture

## Purpose and launch boundary

This document describes Taslim.ai's internal subscription and credit foundation. It intentionally does **not** activate payment collection, connect a payment provider, or expose provider credentials. The current customer-facing charge is zero: the existing Usage Ledger continues to record AI/provider activity and provider-cost accounting, while the Credit Ledger remains an opt-in entitlement ledger with `Billing:CustomerChargingEnabled=false` in both default and production configuration.

The initial plan catalog is:

| Code | Plan | Monthly price | Monthly included credits |
| --- | --- | ---: | ---: |
| `free` | Free | $0 | 1,000 |
| `pro` | Pro | $9 | 10,000 |
| `ultra` | Ultra | $19 | 30,000 |
| `mega` | Mega | $29 | 60,000 |
| `business` | Business | $59 | 150,000 |

Credit quantities are foundation values, not a promise that production AI charging is enabled. Pricing and allowance values are stored as plan data so a future reviewed commercial launch can change catalog policy without coupling the domain to Stripe or another provider.

## Ownership and authorization

Subscriptions, billing periods, entitlements, and credit ledger entries are owned by a `Workspace`. The authenticated user must be a member of the requested workspace before the billing endpoint returns any data. The server never accepts a browser-supplied workspace ID as proof of access. The account UI uses the authenticated personal workspace returned by the existing auth flow, and the API enforces membership through `WorkspaceAccessService`.

A new personal workspace is provisioned with a Free subscription, an open monthly billing period, an IncludedMonthly entitlement, and a matching positive credit grant. The provisioning service returns the latest workspace subscription and uses the subscription/period grant key `included:{subscriptionId}:{billingPeriodId}`; subscription rows remain historical so future plan changes do not erase prior periods.

## Domain model

`Plan` is the provider-independent commercial catalog record. It stores the stable plan code, display name, currency, monthly price, included monthly allowance, active flag, and sort order. Seeded plan IDs are stable so future migrations can refer to the catalog without hard-coding payment-provider identifiers.

`Subscription` belongs to a workspace and references a Plan. It carries status, current period boundaries, next renewal, cancellation-at-period-end intent, and optional future provider metadata. `BillingProvider` and `ProviderSubscriptionReference` are nullable by design and are not populated by this task.

`BillingPeriod` is the auditable period boundary for an active subscription. It records included credits and open/closed/expired state. `CreditEntitlement` identifies the source and lifetime of a grant: included monthly, purchased, or administrative correction. An entitlement has its own idempotency key and optional billing-period/source references.

`CreditLedgerEntry` is the append-only movement record. Positive amounts grant credits; negative amounts debit them. Entry types distinguish grants, usage debits, refunds, reversals, administrative corrections, and expiry. Every entry has a workspace, reason, creation time, unique workspace-scoped idempotency key, and optional links to an entitlement, UsageTransaction, reversing entry, and acting user.

## Usage Ledger versus Credit Ledger

These ledgers must remain conceptually distinct:

* **Usage Ledger (`UsageTransaction`)** records what AI/provider activity occurred: feature, request, provider/model, token or image measurements, provider cost, pricing basis, anomalies, status, and safe operational metadata. It is the source for internal activity and provider-cost accounting.
* **Credit Ledger (`CreditLedgerEntry`)** records what customer entitlement moved: included grants, future purchases, usage debits, refunds/reversals, expiry, and administrative corrections. It is the source for the customer's balance and history.

No provider cost, model, pricing snapshot, or internal cost basis is returned by the billing account endpoint. Existing admin usage reporting remains the place for provider-cost visibility.

A future charging integration should complete a UsageTransaction first, calculate the reviewed customer credit quantity from a versioned policy, and then call `ICreditLedgerService.RecordUsageDebitAsync` with a stable idempotency key such as `usage:{usageTransactionId}`. That method returns no movement while customer charging is disabled. When enabled later, the resulting negative CreditLedgerEntry links back to the UsageTransaction and remains separately auditable.

## Idempotency and corrections

Credit movement commands require an idempotency key. Database uniqueness is enforced on `(WorkspaceId, IdempotencyKey)` for both entitlements and ledger entries. Repeating a grant or debit returns the original movement rather than creating a second movement. This is the primary protection against retries, duplicate worker delivery, and provider webhook replay.

Refunds and corrections are represented as new entries rather than edits or deletes. `ICreditLedgerService.ReverseAsync` loads the original entry within the same workspace, creates the equal-and-opposite movement, links it with `ReversesEntryId`, and uses its own idempotency key. A future provider refund can therefore be recorded without erasing the original charge or usage evidence. Administrative corrections use `CreditEntitlementType.AdministrativeCorrection` and `CreditLedgerEntryType.AdministrativeCorrection`, with an actor and human-readable reason.

The product principle is explicit: a customer must not lose value because of confusion, a system error, a duplicate request, or an unfair charge. Corrections are additive, explainable, and reversible. There is no permanent-delete path for credit movements.

## API and UI foundation

`GET /api/workspaces/{workspaceId}/billing` is authenticated and membership-protected. It returns the current plan, subscription status, period dates, allowance totals, remaining included/purchased/adjustment balances, and the latest credit transaction history. It intentionally omits provider costs and payment-provider details.

The protected `/account/billing` screen provides:

* current plan and status;
* remaining credits and included allowance;
* current billing period and renewal date;
* breakdown of included, purchased, and correction balances;
* a disabled upgrade placeholder;
* credit transaction history with type, reason, amount, and date;
* EN, AR, and KU translations using the existing locale provider and RTL direction behavior.

The existing `/account/usage` screen remains the Usage Ledger surface. Its customer charge value continues to be zero through `SafeUsageChargingService`.

## Administrative and future provider boundary

This task adds no payment collection endpoint and no plan-management UI. The data model supports future administrator tooling through active plans, subscription status, provider-neutral references, period state, actor IDs, and append-only corrections. Any future admin mutation should require an explicit administrator policy, a stable idempotency key, an actor user ID, and a reason. Plan changes should create or close periods and entitlements rather than rewriting historical rows.

A future payment adapter may populate provider fields and translate provider events into subscription state changes, purchases, or refunds. Provider webhooks must be verified by the adapter before calling the domain services. No payment-provider SDK or secret is part of this foundation.

## Migration and integration notes

The migration `AddSubscriptionsAndCreditsFoundation` creates `Plans`, `Subscriptions`, `BillingPeriods`, `CreditEntitlements`, and `CreditLedgerEntries`, seeds the five intended plans, adds ownership foreign keys, and adds uniqueness/index/check constraints for auditability and retry safety.

No Studio implementation was modified. No `UsageTransaction` domain or service file was changed. The only Usage Ledger integration point is the optional, disabled `ICreditLedgerService.RecordUsageDebitAsync` contract, which accepts a UsageTransaction ID but does not alter the existing Usage Ledger behavior. This keeps provider activity accounting and customer entitlement accounting independent while making a later reviewed charging launch straightforward.
