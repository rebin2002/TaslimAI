# Payment and Checkout Architecture

## Launch boundary

Wave 2 establishes a provider-independent payment foundation without enabling real customer charging. `Billing:CustomerChargingEnabled=false` remains committed in both default and production configuration, and the selected provider remains `unconfigured`. The checkout service returns `CHECKOUT_DISABLED` before it creates a session while the flag is false. No provider SDK, credential, secret, or real checkout endpoint is included.

The customer-facing billing screen is intentionally read-only. It displays the Free, Pro, Ultra, Mega, and Business plan catalog, monthly price, included credits, current plan, period renewal, payment status, and future upgrade, downgrade, checkout, and cancellation affordances. Every mutating control is disabled until a reviewed launch changes both the operational configuration and the provider decision. The Usage Ledger and Credit Ledger remain separate.

## Provider abstraction

`IPaymentProvider` owns the provider boundary. It accepts a normalized checkout request, returns a provider session reference and URL, verifies raw webhook signatures through `IWebhookSignatureVerifier`, and parses provider payloads into the internal `ProviderPaymentEvent` contract. A provider implementation must be registered only after credentials, webhook signing policy, amount/currency behavior, retry policy, customer communication, and launch approval are documented. `UnconfiguredPaymentProvider` rejects provider work by default.

`ICheckoutSessionService` is the domain-facing checkout abstraction. It persists an internal checkout session before contacting a future provider, uses a caller-supplied idempotency key, stores the provider session reference only after the provider responds, and records a safe failure reason if the provider request fails. Provider customer references are stored separately from the subscription so they can be reused across future sessions without exposing them to normal billing responses.

## Payment lifecycle data

The migration `20260924013343_AddPaymentFoundation` adds the following tables:

| Table | Purpose | Safety properties |
| --- | --- | --- |
| `ProviderCustomerReferences` | Workspace-scoped provider customer identifiers | Unique provider/customer reference per workspace; no payment details stored |
| `CheckoutSessions` | Internal checkout intent and provider session reference | Workspace/idempotency uniqueness; provider reference uniqueness; safe failure status |
| `PaymentAttempts` | Attempt-level payment state and amount/currency snapshot | Workspace/idempotency uniqueness; provider payment reference uniqueness; failed-payment reason |
| `PaymentEvents` | Verified webhook receipt and normalized event status | Provider event uniqueness; payload hash; signature-verified flag; processed/rejected state |
| `PaymentRefunds` | Refund lifecycle and provider refund reference | Workspace/idempotency uniqueness; provider refund reference uniqueness; append-only reason |
| `SubscriptionLifecycleEvents` | Append-only activation, renewal, failure, and cancellation audit trail | Historical reason and source; subscription/time indexes |
| `PaymentReconciliationRecords` | Provider-versus-local matching and mismatch queue | Unique provider object identity; pending/mismatch/resolved status |

Existing `Plans`, `Subscriptions`, `BillingPeriods`, `CreditEntitlements`, and `CreditLedgerEntries` remain the commercial and entitlement foundation. Historical subscription periods and credit movements are not rewritten.

## Webhook controls

`IPaymentWebhookService` applies controls in this order:

1. Resolve the explicitly selected provider.
2. Verify the raw payload signature before parsing or persisting the event.
3. Parse into a provider-neutral event with a stable provider event reference.
4. Reject a duplicate provider/event reference without applying domain state twice.
5. Persist the verified payload hash and bounded payload for audit and support.
6. Apply only the supported lifecycle transition, recording a rejected status and safe reason if the transition fails.

Payment-success events must match the recorded payment attempt amount and currency. Failed payment events move an active subscription to `PastDue` and append a lifecycle reason. Renewal events create a new billing period and use the canonical `included:{subscriptionId}:{periodId}` credit-grant key. A cancellation at period end closes the lifecycle at renewal rather than erasing the existing period.

## Refund and reversal controls

Refunds are idempotent by workspace and command key and are capped at the remaining refundable amount. The original payment attempt remains intact and changes to `Refunded` or `PartiallyRefunded`; the refund is a separate row with provider reference, amount, status, timestamps, and an auditable reason. If a payment attempt is linked to a credit ledger entry, the service creates an append-only credit refund movement through `ICreditLedgerService`. No payment, refund, subscription, or credit movement is deleted or overwritten to hide history.

The customer-protection principle is explicit: Taslim must not profit from a duplicate request, provider replay, amount mismatch, system failure, confusion, or unfair charge. Idempotency keys, provider-reference uniqueness, signature verification, amount/currency matching, refund caps, append-only credit movements, and reconciliation records are the primary controls.

## Future configuration and integration sequence

Before enabling charging, the operator must select and approve one provider, add server-only credentials and webhook signing configuration, register its `IPaymentProvider` implementation, set a production webhook route protected by the provider verifier, validate plan-to-provider price mapping, define customer communication and retry behavior, and run a staged reconciliation test. Only after those steps should `Billing:Provider` change from `unconfigured` and `Billing:CustomerChargingEnabled` be considered for a controlled launch.

The first production integration should create a provider customer reference, create an internal checkout session, call the provider with the same idempotency key, persist a provider session reference, and wait for a verified webhook before activating a paid subscription or granting purchased credits. Provider API responses must never be trusted as a replacement for verified webhook state. A support/reconciliation worker can populate `PaymentReconciliationRecords` and resolve mismatches with a reason; it must never silently mutate a ledger.

## Validation

The API test suite covers disabled checkout, idempotent payment attempts, failed-payment state, append-only refunds, signed webhook processing, and webhook replay protection. The web suite covers the existing regression set and the production build. EF Core migration generation and model validation must be run against the API project directly because the repository intentionally contains project files rather than a solution file.
