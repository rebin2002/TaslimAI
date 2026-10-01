# Wave 5 — Character Production Sheets

Character Production Sheets are an additive layer on the existing Movie Studio **Character Bible**. They do not replace character cards, states, relationships, continuity facts, or locks.

## Contract

Each sheet has one current version and immutable prior versions. A version contains five explicit identity-reference slots:

| Slot | Purpose |
| --- | --- |
| Canonical identity face | The single authoritative face identity reference |
| Body | Full-body proportion and silhouette reference |
| Front | Front-facing pose/reference |
| Side | Side profile/reference |
| Back | Back profile/reference |

The API rejects duplicate asset IDs across these five slots. This is a contract-level guard against ambiguous duplicate-face reference compositions. Asset IDs must also belong to the movie workspace.

A version may include up to twelve wardrobe/look variants. Look keys are unique within the version, and each look can carry wardrobe, appearance, continuity notes, and an optional asset.

## Lifecycle

1. **Draft** — editable and versioned; saving creates a new version rather than mutating history.
2. **Approved** — requires all five identity-reference slots; approval records user and time.
3. **Locked** — prevents new versions until an explicit unlock; the lock records user and time.
4. **Unlock for revision** — returns the current version to Draft without deleting prior versions.

## Provenance and continuity

Every saved version stores provider-neutral provenance JSON and a deterministic SHA-256 hash. Provenance includes the Character Bible source, character ID, source character update time, version number, slot mapping, look inputs, and an optional user note. No provider, model, or prompt names are exposed in the normal-user contract.

The existing Character Bible read model now includes the production-sheet status/current version/history projection. Existing clients can continue to omit the optional projection.

## Routes

- `GET /api/movie-studio/characters/{characterId}/production-sheet`
- `PUT /api/movie-studio/characters/{characterId}/production-sheet`
- `POST /api/movie-studio/production-sheets/{sheetId}/approve`
- `POST /api/movie-studio/production-sheets/{sheetId}/lock`
- `POST /api/movie-studio/production-sheets/{sheetId}/unlock`

Mutating routes retain the existing authenticated collaboration permission and anti-forgery boundaries.

## Cost-efficient filmmaking alignment

The sheet is a cheap, reviewable reference package before any expensive video work. Generated clips remain downstream raw footage; regeneration should target missing inserts or selected ranges, while this sheet provides stable character identity, wardrobe, and spatial reference inputs for continuity-aware planning.
