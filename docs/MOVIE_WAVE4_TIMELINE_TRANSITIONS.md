# Movie Wave 4: Timeline transitions and edit decisions

## Contract

`apps/api/Movies/MovieTimelineTransitions.cs` defines `movie-timeline.v1` as a provider-neutral contract for the canonical movie timeline. A timeline contains ordered clip entries and typed transitions:

- `cut`: two adjacent clips, zero duration, exact boundary.
- `dissolve`: two adjacent clips with an exact overlap, positive duration no longer than either clip.
- `fade_in`: incoming clip only, starting at that clip's start.
- `fade_out`: outgoing clip only, ending at that clip's end.

Timing is expressed in decimal timeline seconds. The validator does not infer, normalize, reorder, or call a provider.

## Authority and provenance

`MovieDirectorTransitionRecommendationContract` is advisory metadata. It can describe a proposed transition but cannot be applied by itself. `MovieUserTransitionOverrideContract` records the explicit user choice and reason. `MovieTimelineEditDecisionContract` carries both records separately when applicable and is evaluated against the canonical timeline id and version.

`MovieTimelineAuthority.ApplyUserOverride` is the only mutating operation in this layer. It requires a user override, validates the base version, applies add/update/remove, increments the canonical timeline version, and validates the resulting snapshot. This prevents stale Director output from becoming assembly input and keeps the canonical timeline authoritative.

## Integration notes

- The contracts are pure and do not activate billing, media providers, generation jobs, or external services.
- No database migration is required. Existing `MovieAssembly` and `MovieClip` persistence remains unchanged until a later assembly persistence task explicitly adopts this contract.
- Assembly code should read the latest validated `MovieCanonicalTimelineContract`; it should not consume Director recommendation payloads directly.
- A future persistence/API adapter should use the existing Movie authorization and CSRF boundaries and store recommendation/override provenance separately from the canonical timeline snapshot.
- Tests are deterministic unit tests and do not use external generation calls.
