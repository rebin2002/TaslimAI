# Movie Production Reference Package

Wave 4 adds a deterministic, provider-neutral read model for the references needed to produce one movie shot:

`GET /api/movie-studio/shots/{shotId}/production-references`

The endpoint is authenticated and workspace-authorized. It composes the existing Wave 1–3 records; it does not create a generation job, invoke a provider, select a model, expose a prompt, or change billing/accounting behavior.

## Package contents

`MovieProductionReferencePackageDto` is schema-versioned (`1`) and contains:

- **Guide** — the locked Movie Guide revision when one exists, limited to visual, cinematography, and continuity sections;
- **Style** — project style plus bounded guide-derived visual, camera, palette, and continuity values;
- **Characters** — the existing character continuity projection and reference asset IDs;
- **Wardrobe** — the selected character-state wardrobe, then character-card wardrobe, with a continuity lock taking precedence over both;
- **World** — bounded set, variation, fact, lock, and conflict data from `MovieWorldContinuityProjector`;
- **Locations** and **Props** — only entities used by the target shot or scene, with their existing reference asset IDs;
- **Keyframe** — the latest approved `ProductionKeyframe` version for the shot, if available;
- **PreviousShots** — up to four immediately preceding shots in movie sequence order, with selected/final take and approved-keyframe pointers;
- **Warnings** — safe, bounded continuity or readiness warnings.

Each section has a SHA-256 `SourceHash`, source type/id, optional source version, captured timestamp, and explicit precedence label. The package itself has a stable `PackageHash` that excludes request capture time, so repeated reads are comparable while the response may still report when it was assembled.

## Precedence

1. A valid `Locked` revision identified by `MovieContinuityGuide.LockedRevisionNumber` is authoritative for guide-derived style values.
2. For wardrobe, an active character continuity lock wins over the applicable character state, which wins over the character card.
3. World lock and fact precedence remains owned by `MovieWorldContinuityProjector` and its existing conflict detector.
4. Only approved keyframes are exposed as production keyframe references. If none exists, the package returns `approved_keyframe_missing` rather than inventing a candidate.

An unlocked guide is not silently treated as authoritative. The package remains available for planning, but emits `guide_not_locked` so a production gate can require guide approval.

## Bounds and persistence

The package is a deterministic read model over canonical persisted Movie Guide, Cast, World, Shot, Production Version, and Take data. No table or migration is required. Limits include 32 characters, 16 locations, 16 sets, 32 props, 4 previous shots, 64 warnings, bounded guide sections, and a 100,000-character serialized package ceiling. Oversized guide JSON is represented by a bounded truncation marker while scalar values are still resolved from the authoritative locked revision.

The normal-user DTO contains no provider key, model identifier, credential, raw execution handle, or prompt. Existing generation lifecycle, usage ledger, authorization, and disabled-provider defaults remain unchanged.
