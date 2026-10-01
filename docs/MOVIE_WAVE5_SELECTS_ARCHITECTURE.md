# Movie Wave 5: Selects and subclips

## Purpose

A generated `MovieTake` remains raw footage until the existing take review and selected/final lifecycle says otherwise. This Wave 5 core adds a second, narrower edit decision: a `MovieTakeSelect` marks a usable time range inside one take without approving, rejecting, selecting, or finalizing the whole take.

The feature is provider-neutral. It stores millisecond boundaries and safe Taslim-owned references only; provider, model, prompt, credential, and raw upstream fields are not part of the public contract.

## Canonical relationships

```text
MovieProject → MovieScene → MovieShot → MovieTake
                                      └→ MovieTakeSelect (0..n)
MovieTimelineItem ──SourceTakeId──────────────┘
                 └─SourceSelectId (optional)
```

`MovieShot.SelectedTakeId`, `MovieShot.FinalTakeId`, `MovieTake.SelectedAt`, and the existing approved/selected take status remain the only authority for whether visual footage may enter the timeline. A select does not replace those pointers.

## Select lifecycle

| State | Meaning |
| --- | --- |
| `Draft` | Range exists and is editable, but cannot enter the canonical timeline. |
| `Approved` | The range may be referenced by a visual timeline item, subject to the parent take's existing selected-take rules. |
| `Rejected` | The range is explicitly not approved; the parent take remains unchanged. |
| `Archived` | Retained history that cannot be reviewed again. |

Creation requires Movie edit permission and an owned, active generated video Asset with trusted duration metadata. A select has a positive half-open `[start, end)` millisecond range inside that source duration. The service assigns a per-take `SelectNumber`, so multiple selects are supported without overwriting the take.

Review requires Movie approval permission and records `ReviewedByUserId`, `ReviewedAt`, and a bounded review note. Approving or rejecting a select never mutates `MovieTake.Status`, `SelectedTakeId`, `FinalTakeId`, or take approval records.

## Provenance and ownership

Every select stores a bounded provenance envelope containing only:

- source kind (`movie_take`);
- `MovieTakeId`, `MovieClipId`, and source `AssetId`;
- persisted start/end boundaries; and
- capture time.

The row is owned through `MovieTake → MovieShot → MovieScene → MovieProject`, and also records its creator and reviewer. Foreign keys use cascade deletion from the take and restricted user deletion so history cannot silently lose authorship while a take exists.

## Timeline behavior

`MovieTimelineItem.SourceSelectId` is additive and nullable. Existing timeline items continue to use `SourceTakeId` and their explicit trim boundaries.

When a select is supplied:

1. the select must belong to the supplied take, or supplies the take when `SourceTakeId` is omitted;
2. the select must be `Approved` and belong to the same Movie project;
3. the parent take must still satisfy the existing selected/final take and approved/selected status checks;
4. the timeline item source in/out points are taken from the persisted select boundaries; conflicting caller-supplied boundaries are rejected; and
5. the source asset remains an approved active video Asset under the existing timeline source rules.

This lets the timeline salvage a usable range from raw footage while retaining the canonical selected-take semantics and without copying or mutating media.

## Persistence and API

The additive `AddMovieTakeSelects` migration creates `MovieTakeSelects`, adds a nullable `SourceSelectId` to `MovieTimelineItems`, and adds range, ownership, status, and lookup indexes. API routes are project-scoped:

- `GET /api/movie-studio/projects/{movieProjectId}/takes/{takeId}/selects`
- `POST /api/movie-studio/projects/{movieProjectId}/takes/{takeId}/selects`
- `POST /api/movie-studio/projects/{movieProjectId}/takes/{takeId}/selects/{selectId}/review`

All mutating routes retain the established authenticated, CSRF-protected Movie controller boundary. No provider or paid generation adapter is introduced.
