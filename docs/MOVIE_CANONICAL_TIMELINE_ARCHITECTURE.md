# Movie Canonical Timeline

## Scope

Wave 4 adds one authoritative, project-scoped edit model for Movie Studio. `MovieTimeline` is a non-destructive assembly of existing canonical Movie V2 records; it does not create a second shot hierarchy, copy media, invoke a provider, or publish an output asset.

```text
MovieProject
  └── MovieTimeline
        └── MovieTimelineRevision (append-only)
              └── MovieTimelineTrack (Video / Audio / Captions)
                    └── MovieTimelineItem
```

## Source rules

- `VisualTake` items reference an approved `MovieTake` that is selected or finalized by the canonical `MovieShot` pointer. The resolved source must be an active video `Asset` with a private stored file.
- `AudioAsset` items reference an active, approved `audio` or `music` `Asset` with a private stored file.
- `CaptionAsset` items reference an active, approved WebVTT, SRT, or TTML asset. Caption files may remain the existing generic `file` asset type; MIME/extension identifies the caption representation without changing the global asset taxonomy.
- Asset approval is backward-compatible with the current Asset domain: active assets without an explicit negative approval marker are eligible; metadata with `approved: false` is rejected, and an explicit `approvalState` must be `Approved`.
- Source duration is read from bounded asset/take metadata, the selected Movie clip, or the canonical shot duration. A source out-point must be known and bounded so revision duration is deterministic.

## Time and overlap invariants

All time values are integer milliseconds. Each item stores both source and timeline in/out points plus the derived duration. The database enforces `duration = timelineOut - timelineIn`; the service validates source bounds and requires positive ranges.

Items cannot overlap on the same track. Overlap across different tracks is valid and is required for normal picture/audio/caption assemblies. A `Gap` item has no source reference and occupies an explicit positive timeline range.

Revision duration is the maximum timeline out-point across all tracks, so it is stable across reads, clones, and reloads.

## Revision and locking lifecycle

Creating a revision never mutates prior items. A revision may be created from the current or explicitly named base revision; when no track payload is supplied, the service deep-clones tracks and items with new IDs while preserving source provenance and timing. Locked revisions are immutable. A later edit must create a new draft revision, which becomes the current revision without altering the locked history.

The final-approval permission is required to lock a revision. Locking records actor and timestamp on both the revision and timeline root. Locking is a hand-off boundary only; it does not render or assemble media.

## API

- `GET /api/movie-studio/projects/{projectId}/timeline`
- `POST /api/movie-studio/projects/{projectId}/timeline/revisions`
- `POST /api/movie-studio/timeline/revisions/{revisionId}/tracks`
- `POST /api/movie-studio/timeline/tracks/{trackId}/items`
- `POST /api/movie-studio/timeline/revisions/{revisionId}/lock`

All mutations use the existing Movie Studio CSRF and collaboration boundaries. Provider/model/prompt/credential fields are absent from the normal-user timeline contract. Wave 4 keeps Movie video providers, billing, and external generation disabled.
