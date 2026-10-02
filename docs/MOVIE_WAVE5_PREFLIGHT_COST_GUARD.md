# Movie Studio Wave 5 — Preflight Cost Guard

## Purpose

The production preflight guard is a provider-neutral, read-only hand-off check for expensive movie production renders. It connects the existing canonical reference package, adaptive-resolution director, server-side movie cost estimator, and generation budget guardrails without enabling a provider or changing charging.

The guard is exposed at:

```text
GET /api/movie-studio/shots/{shotId}/production/preflight
```

Optional query fields are `sourceVersionId`, `targetResolution`, and `qualityTier`. The response is safe for normal users and contains no provider name, model name, prompt, credential, endpoint, or upstream execution identifier.

## Decision policy

Before a production render can be queued, the server re-evaluates the same preflight used by the read-only endpoint:

- an approved storyboard must exist;
- an approved production keyframe must exist;
- the selected source version must be an approved motion preview;
- the canonical reference package must have no blocking continuity conflict;
- the adaptive-resolution recommendation must meet its quality confidence floor or explicitly require QC/escalation review;
- the existing server-trusted cost guardrail must allow the request, including any configured confirmation or cap decision.

The ordinary shot-plan readiness result remains visible as an advisory check so this additive gate does not invalidate existing production records that already passed the canonical stage workflow. Missing plan fields are still recommended before production.

The guard deliberately treats `guide_not_locked` and non-blocking continuity warnings as recommendations rather than silently ignoring them. This preserves the reference-package precedence model while keeping the existing disabled-provider test flows backward compatible. Blocking/error/critical continuity findings stop the render.

## Cost-efficient filmmaking behavior

The adaptive plan is calculated once through the shared `MovieProductionAdaptivePlanBuilder`, used by both preflight and the render orchestrator. It reports only Taslim-owned intent:

- source resolution;
- target resolution;
- processing path;
- quality tier;
- deterministic quality confidence and floor;
- QC escalation state and source-resolution alternative;
- bounded reason codes.

The plan does not invent a price. The movie cost estimator consumes only server-side capability/pricing metadata. Missing metadata remains unknown or unevaluated and is never converted to zero. Existing `GenerationCostGuardrailService` then decides whether a known estimate is within the configured safety envelope. Customer charging remains zero through the existing `SafeUsageChargingService`, and `MovieVideo.Enabled` remains false.

The recommendations prioritize cheap reference work—storyboard, source keyframe, and motion preview—before a production video pass. This supports salvaging short previews and approved references rather than regenerating a whole scene.

## Persistence and compatibility

No database tables, columns, or migrations are required. The endpoint is a read-only projection, and the queue-time recheck occurs before `MovieClip` or `GenerationJob` creation. Existing generation-job accounting, selected-take-only mastering, timeline, provenance, recovery, authorization, and provider-disabled defaults remain unchanged.

## Focused coverage

`MovieProductionPreflightTests` verifies that an incomplete shot receives a safe `reference_work_required` decision, recommends the missing keyframe work, returns adaptive intent, and redacts provider/model/prompt fields. Existing production-render acceptance coverage continues to exercise the approved storyboard → keyframe → motion preview → explicit render path.
