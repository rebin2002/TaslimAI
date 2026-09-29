# Provider Capability Registry

## Scope

The API now has a versioned, server-side registry for video generation, video upscaling, and video mastering capabilities. The registry describes what a provider/model route declares it can support; it does **not** select an adapter, enable a provider, call an external API, calculate customer price, or charge credits.

The default catalog is intentionally empty:

```json
{
  "SchemaVersion": 1,
  "CatalogVersion": "unconfigured-v1",
  "Definitions": []
}
```

No provider is activated by adding the registry service. Existing `IMovieVideoProvider` registration and its disabled-by-default behavior remain unchanged.

## Contract

`ProviderCapabilityDefinitionOptions` supports:

- capability kind: `video-generation`, `video-upscaling`, or `video-mastering`;
- internal provider/model keys and a definition version;
- declared availability, supported width/height pairs, aspect ratios, exact durations, and feature keys;
- typed input/reference/duration constraints;
- an optional cost metadata hook (`RateKey`, unit, pricing version, effective date, and source) that can be handed to the existing internal cost estimator later;
- provenance (`Source`, captured timestamp, and bounded notes).

The catalog has its own `SchemaVersion`, `CatalogVersion`, effective timestamp, and source. Definitions are normalized into an immutable `ProviderCapabilityRegistrySnapshot` when the singleton is created.

## Validation

`ProviderCapabilityRegistryOptionsValidator` rejects malformed catalogs, including:

- unsupported schema or capability versions;
- duplicate provider/model/capability definitions;
- missing version, resolution, aspect, duration, constraint, or provenance metadata;
- invalid resolutions, aspect formats, duration bounds, duplicate values, or input limits;
- known cost hooks without a rate key, unit, pricing version, or source.

`IProviderCapabilityRegistry.Validate` matches a provider-neutral request against only available definitions and applies resolution, aspect, duration, feature, input-asset, and reference-count constraints. Failure codes are generic and do not contain provider or model identifiers.

## Security and integration boundary

The registry is registered as `IProviderCapabilityRegistry` in the API DI container only. There is no controller, browser DTO, frontend route, or public readiness endpoint for this catalog. Provider keys, model keys, cost metadata, provenance, and raw configuration therefore remain server-side/admin-only.

A future movie/upscaling/mastering router may use `Validate` before passing an opaque internal candidate to an adapter. That router must continue to use the existing resilience, generation-job, output-validation, private-storage, usage, and administrator-only observability boundaries. It must not copy registry fields into normal-user responses.

## Persistence and activation status

No database migration is required. This is configuration-backed metadata and uses the existing `GenerationCostEstimator` as the future cost integration point rather than introducing a second pricing system. The default configuration has no definitions, no credentials, and no provider activation. No external provider request is made by this branch.
