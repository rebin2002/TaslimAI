# Usage Accounting and Admin Reporting

## Purpose

Batch 3.9 extends Taslim's existing `UsageTransaction` ledger into a provider-cost accounting foundation without activating customer billing. The ledger remains the source of truth for AI activity across chat, asynchronous generation jobs, and Image Studio. Customer charges remain zero through the existing `SafeUsageChargingService`.

## Ledger model

Each transaction is linked to a workspace and user and may be linked to a project, conversation, or `GenerationJob`. The ledger records the Taslim feature, provider and model identifiers, measured token quantities, image input/output quantities, provider latency, provider cost, customer charge, currency, cost basis, failure code, refund time, and a historical pricing snapshot. Provider and model fields are internal operational data; they are not included in normal workspace usage responses.

The transaction state machine is `Pending → Completed`, `Pending → Failed`, `Pending → Cancelled`, and `Completed → Refunded`. Idempotent requests reuse the existing workspace/request/feature uniqueness key. Completed and refunded transactions are terminal for normal completion/failure updates. A cancellation always records zero customer charge, and a refund records the refund timestamp.

## Pricing snapshots and cost formulas

Chat costs are calculated from the selected model's input, cached-input, and output token quantities and are marked `Estimated` because the chat catalog calculation is Taslim's accounting calculation. Image costs prefer measured provider usage from the image response and retain the configured estimate only when the provider does not return usable billable quantities. Each completed transaction records `Actual` or `Estimated` in `CostBasis`, so an estimated fallback is never presented as exact provider-reported cost.

Every completed paid-provider transaction records the pricing version, effective date, source, unit, currency, and rates that were used or configured for the calculation. This prevents a future rate change from changing the interpretation of historical transactions. The Batch 3.9 image snapshot is `gpt-image-2.5-sunburst-2026-09-08`, effective 2026-09-08, with the official rates of $5.00 per million text input tokens, $1.25 per million cached text input tokens, $8.00 per million image input tokens, $2.00 per million cached image input tokens, and $30.00 per million image output tokens. The source is the official [GPT Image 2.5 Sunburst model page][1]. The audit notes are in [`docs/openai-usage-pricing-audit-2026-09-22.md`](openai-usage-pricing-audit-2026-09-22.md).

The Images API response exposes ordinary `usage.input_tokens` and `usage.output_tokens`, plus optional `input_tokens_details.image_tokens` and `output_tokens_details.image_tokens`; it does not guarantee image-specific output details. Taslim therefore persists standard input/output quantities whenever returned and leaves `ImageInputTokens` or `ImageOutputTokens` null when the corresponding detail is absent. For the selected Sunburst response, the billable output quantity is the standard `output_tokens` field, not an invented image-output value. This follows the official [Create image API reference][2].

## Guardrails and anomaly detection

`UsageCostControl` provides a preflight extension point for single-operation, daily-workspace, and monthly-workspace ceilings. It also provides anomaly flags for a single expensive transaction, a daily workspace threshold, or repeated failures in a configured window. Guardrails are explicitly disabled in the committed development and production configuration, so Batch 3.9 does not unexpectedly block existing production workflows. An operator can enable them through `UsageControls` after selecting appropriate limits.

Preflight estimates are stored on the pending ledger transaction. A completed operation replaces the estimate with measured provider cost and retains the pricing snapshot. Safety failures return stable, generic error codes and do not expose provider credentials, prompts, or raw exception details.

## Authorization and privacy

Normal users may read only their own workspace's usage summary and history. Those contracts expose Taslim-level feature and status information, token quantities, and customer charge; they intentionally omit provider cost, provider name, model name, pricing snapshots, and internal anomaly metadata.

Admin reports are served only by the `TaslimAdministrator` Identity role through the `TaslimAdminUsage` authorization policy. The handler checks the role server-side with `UserManager` rather than trusting an email address or a client-supplied claim. The optional `Admin:BootstrapEmails` configuration can grant the role to already-registered accounts during a migration-enabled production startup; it never creates users or grants access from an email alone. Admin reporting includes provider and model details, pricing snapshots, failure and anomaly fields, workspace/user aggregates, and transaction inspection.

## Reporting API and dashboard

The admin endpoints are:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/admin/usage/report` | Summary, daily/feature/workspace/user/status breakdowns, and paginated recent transactions. |
| `GET /api/admin/usage/summary` | Summary only. |
| `GET /api/admin/usage/breakdowns` | Aggregated breakdowns only. |
| `GET /api/admin/usage/transactions` | Filtered, paginated internal transaction list. |
| `GET /api/admin/usage/transactions/{id}` | Internal transaction inspector. |

All report queries accept bounded UTC date ranges and optional feature, status, workspace, user, generation-job, page, and page-size filters. The protected web surface is `/account/admin/usage`; it provides Today, 7-day, 30-day, and custom date ranges, daily trend bars, feature/workspace/user breakdowns, recent transactions, and an internal inspector. It does not replace the normal workspace Usage view.

## Migration and operations

`AddUsageAccountingFoundation` and `AddUsageCostBasis` are additive. They preserve the existing `DataProtectionKeys`, Generation Jobs, Assets, Stored Files, Identity, and usage tables and add nullable provenance/metadata columns, bounded internal JSON fields, indexes for report filters, the optional Generation Job foreign key, and the nullable cost-basis field. The migrations must be applied through the existing production advisory-lock migration runner. No Redis, queue broker, or new billing provider is required.

## Future extensions

Future customer billing can implement a different `IUsageChargingService` and a currency-aware settlement policy without changing provider handlers. Future pricing catalogs can publish new versioned snapshots while retaining old snapshots on existing transactions. Guardrails can be enabled with explicit operator-selected limits, and a support workflow can review anomalies without exposing internal fields to ordinary users.

[1]: https://developers.openai.com/api/docs/models/gpt-image-2.5-sunburst "Official GPT Image 2.5 Sunburst model page"
[2]: https://developers.openai.com/api/reference/resources/images/methods/generate/ "Official Create image API reference"
