# Movie Wave 5: Salvage Director

`MovieSalvageDirector` implements the **salvage-before-regenerate** planning boundary. It turns a supplied QC or edit issue plus bounded editorial evidence into a deterministic, explainable, ordered repair plan.

## Contract

- Contract version: `movie-salvage-director.v1`
- Planner: `apps/api/Movies/MovieSalvageDirector.cs`
- Read-only API seam: `POST /api/movie-director/salvage-plan`
- No provider, model, prompt, credential, generation-job, asset, usage, or billing call is made.
- The planner does not inspect media or infer evidence. `MovieSalvageEditContext` flags must be supplied by an existing QC/edit workflow.

The response contains only product concepts: issue type, repair action, scope, rationale, preconditions, risk, and whether the action uses existing footage or requires a bounded new-footage repair. No provider metadata is returned.

## Policy ordering

The planner emits at most eight options and always keeps full regeneration last:

1. **Trim** a measured usable range.
2. **Crop** only when a safe crop has been established.
3. **Alternate select** only when an alternate is continuity-safe and eligible for canonical selection.
4. **Reaction insert** for a bounded missing reaction beat.
5. **Insert** only the missing story beat.
6. **Cover angle** for a bounded defective visual moment.
7. **Sound bridge** when usable audio can carry the join.
8. **Transition** as a final editorial cover at an explicit canonical timeline boundary.
9. **Regenerate** the affected shot only as the review-gated fallback. It is clipped by the eight-option bound and is never executed by this planner.

Issue-specific priority is deterministic. Continuity issues prefer an existing continuity-safe alternate before any new footage. Composition issues prefer a safe crop. Duration issues prefer trim. Audio issues prefer a sound bridge. Selection issues prefer alternate selection. Unknown issues use the conservative general order.

## Reference and continuity guardrails

- Existing-footage repairs preserve the canonical take and do not start generation.
- Reaction, insert, and cover-angle options are explicitly marked as requiring bounded new footage and a locked reference package.
- If references are not locked, the plan sets `referenceLockRequired` and includes the lock requirement in the option preconditions; it still does not call a provider.
- The fallback regeneration option always carries explicit approval, locked-reference, normal QC, accounting, and recovery prerequisites.
- Selection never means “latest created take.” It requires a supplied continuity-safe alternate and leaves canonical selection to the existing selected-take workflow.
- Timeline transitions remain advisory planning output; the canonical timeline and existing user-override authority remain the source of truth.

## Persistence and compatibility

This wave is intentionally **migration-free**. Plans are computed from the request and are not persisted, so there is no second source of truth beside Movie V2 takes, selected/final pointers, canonical timeline revisions, provenance, usage accounting, or recovery checkpoints. A future persistence layer must store the plan as advisory provenance and must not make it an execution or publication path.
