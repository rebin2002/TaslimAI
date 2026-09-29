# Movie Wave 3 — Real-Time Generation Cost Estimator

This change adds a server-trusted, provider-neutral cost-estimation seam for movie generation. It does not enable a provider, perform generation, charge a customer, or expose provider/model keys to normal-user responses.

## Data and calculation

`ProviderCapabilityPricing` is persisted in `ProviderCapabilityPricings` and is matched by the requested:

- duration and metadata duration cap;
- source and target resolution;
- quality tier;
- processing path;
- retry count and metadata retry cap;
- upscaling intent and pass count.

The estimator multiplies the persisted base per-second range by duration and billable attempts (`1 + retries`). If upscaling is requested, persisted upscaling rates are added per pass. Optional fixed fees are supported. Each result is rounded to eight decimal places using away-from-zero midpoint rounding.

The safe estimate JSON contains only state, currency, range, and generation/upscaling components. Catalog version/source fields remain server-side and are ignored during JSON serialization.

## States

| State | Meaning |
| --- | --- |
| `estimated` | At least one eligible route has complete pricing and the result is below the configured safety cap. |
| `unknown` | A route exists but pricing is incomplete, currencies conflict, the request exceeds an estimator cap, or the result exceeds the configured estimate cap. |
| `unevaluated` | Capability metadata or an eligible route is not available. |

Unknown and unevaluated results never become zero-cost estimates.

## Server trust boundary

Generation cost fields on generic, movie, and selective-regeneration request models are both JSON-ignored and MVC model-binding-ignored. Movie queue and regeneration flows calculate estimates from persisted metadata and server-selected provider routing. The upper bound is used for existing budget preflight compatibility; the complete safe range is persisted as `CostEstimateJson`.

Charging remains disabled by configuration. No pricing seed rows are included in this migration; operators must load current capability/pricing metadata through a separately controlled internal process.

## Wave-2 compatibility

The current `main` branch does not contain the Wave-2 Adaptive Resolution Director implementation. This branch therefore uses the same provider-neutral vocabulary (`source resolution`, `target resolution`, `quality tier`, and `processing path`) as a compatibility seam, with safe defaults for existing callers. No Wave-2 branch is merged here.
