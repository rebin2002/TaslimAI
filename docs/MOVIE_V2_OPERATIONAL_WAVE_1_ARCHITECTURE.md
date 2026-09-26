# Movie Studio V2 — Operational Wave 1 Architecture

**Integration branch:** `integration/movie-v2-operational-1`  
**Production base:** `071e9c2e47a3de08a8ecf780a8153afdcda8a77b`  
**Scope:** Approved Operational Wave 1 branches 1–20

## Purpose

Operational Wave 1 turns Full Movie from a planning shell into a persisted, reviewable production workflow. Every consequential step is represented by a durable record, explicit approval boundary, and server-side authorization check. Provider-backed generation remains disabled unless a provider is safely configured; the UI never fabricates footage or claims quality-control evidence without persisted records.

## Canonical workflow

```text
Story revision
  → production Scene hierarchy
  → Shot Plan
  → Storyboard Candidate
  → Approved Storyboard
  → Production Keyframe
  → Approved Keyframe
  → Motion Preview
  → Production Render
  → MovieTake
  → Approved / Selected / Final Take
```

`MovieProductionVersion` represents a candidate, artifact, or version in this funnel. `MovieTake` represents the canonical rendered take. Selective regeneration creates a new request, generation job, version, and downstream review state; it does not destructively overwrite prior artifacts.

## Bounded Movie workspace

The shared Full Movie shell is intentionally small and keeps the center work area dominant with a secondary Director/Inspector rail. Room navigation uses bounded read models rather than loading the complete Movie project graph:

- **Overview:** project status, progress, approvals, cost/readiness signals, warnings, and next actions.
- **Story:** editable Story revisions and screenplay structure. Approved revisions remain immutable; Director assistance produces a reviewable editable AI-suggested revision.
- **Cast:** canonical Character Cards, states, relationships, reference assets, and continuity locks.
- **World:** Locations, Sets, Variations, Props, Facts, Locks, references, and usage details.
- **Scenes:** `MovieProject → Act → Sequence → Scene → Shot`, including screenplay breakdown and shot planning.
- **Storyboard:** candidate creation and explicit review for real shot plans.
- **Production:** keyframe, motion-preview, render, execution/QC signals, MovieTake approval, selection, and finalization.
- **Edit, Audio, QC, Exports, Team:** honestly marked as future surfaces where Wave 1 does not provide an operational workflow.

Room-specific payloads are fetched on demand and concurrent room requests are deduplicated without caching completed responses. Lightweight read models omit large metadata and continuity graphs unless the active room requires them.

## Continuity and Director context

Character and World continuity consume the Cast and World canonical records and produce bounded, target-specific snapshots. They do not create duplicate hierarchies or lock systems.

The Movie Director consumes:

- the locked Movie Guide;
- an approved Story where available;
- Character Continuity and World Continuity snapshots;
- cinematography and shot-plan intent;
- production state; and
- collaboration/approval state.

Director behavior is always **Review → explicit approval → execute**. Auto Director quality recommendations remain distinct from explicit Fast, Standard, Cinematic, and Studio selections. Director actions cannot silently mutate an approved Story revision or bypass production approval boundaries.

## Authorization and approvals

`MovieAuthorizationService` is the canonical server-side policy. Frontend capability checks are UX-only and are exposed through the project capabilities endpoint; they do not grant access. The API must fail closed for unauthorized and cross-workspace access.

The following approval domains remain separate:

- Story approval;
- production-version/keyframe approval;
- storyboard approval;
- MovieTake approval, selection, and finalization;
- collaboration review decisions; and
- Director proposal approval and action execution.

Reviewer access does not imply Generate, Manage Team, or Manage Budget permissions. Locked Guide, Cast, and World facts cannot be silently mutated.

## Persistence and migrations

Wave 1 migrations include the approved Movie Story/Screenplay, Character Cards, World, World Variations, Collaboration, Director, cinematography, shot planning, selective regeneration, Character Continuity, Production Keyframe references, and targeted Director Context changes. The integrated tree also retains the existing Movie V2 reconciliation migrations and model snapshot.

Before release, the complete migration chain must be exercised against real PostgreSQL in both modes:

1. fresh database from zero; and
2. upgrade from the production/base schema at the authoritative base commit.

EF must report no pending model changes. Generated idempotent SQL must be reviewed for duplicate DDL, destructive operations, incompatible nullability, duplicate indexes/foreign keys, and repeated backfills.

## Provider-safe E2E strategy

The Task 20 harness uses persisted operational contracts and provider-safe test fixtures. It must exercise Story, Cast, World, Scenes, Shot Planning, Camera & Look, Storyboard, Keyframe, Production, Takes, Director approval boundaries, reload/persistence, authorization roles, isolation, and Quick Movie separation without paid provider calls. Generated-looking outputs are valid only when backed by persisted Asset records and associated jobs.

## Wave 2 boundary

The following remain future work beyond this wave: a deeper editorial Edit workflow, Audio, QC evidence workflows, Exports, and deeper Team/collaboration operations.
