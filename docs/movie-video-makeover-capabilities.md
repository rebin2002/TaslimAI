# Movie Studio — AI Video Makeover Capability and Architecture Spike

Issue: #698 (AI Video Makeover — studio swap, relight, restyle, outfit change)
Status: provider-neutral architecture and capability documentation only. No provider is enabled, no credential or secret was added, no paid model call or entitlement probe was made, no production configuration was changed, and no source file outside this document and its regression test was touched.
Verification date for all external claims: 2026-11-12 (public official API documentation only).

## Evidence labels

Every capability statement in this document carries exactly one label:

- `V` — Verified published fact: stated directly by the cited official API documentation page.
- `C` — Vendor product claim: stated on an official vendor product/marketing page, not by API documentation. Must be re-tested before it can become `V`.
- `U` — Unknown: not established by the reviewed public material. Requires vendor confirmation or a controlled paid test with explicit approval.
- `N` — Not supported / not documented as an API operation by official documentation as of the verification date.

A capability is never promoted by inference. In particular, no route is marked as verified for source-audio passthrough, exact frame-rate preservation, or identity/gesture/lip-sync preservation unless the cited official API documentation says so.

## Non-negotiable acceptance rules

The following rules define a passing "makeover" result. They are product requirements, not provider features. Any route that cannot satisfy them is a component, never a shippable feature by itself.

1. `identity-preservation` — the subject's face, body, hair, hands, and voice remain the source subject's. No face swap, no person substitution, no voice replacement. Face/person replacement operations available in provider catalogs (for example Volcengine `person_replace`) are explicitly out of scope and must be rejected by routing, not offered. `U` as a guaranteed provider property on every reviewed route.
2. `speech-preservation` — the spoken content, its timing, and lip movement must not be re-generated. Provider documentation does not guarantee this for any in-place edit route. Where a route generates its own audio (`native`), the source audio must be re-attached server-side and the visual result must be measured for lip-sync drift before use. `U`.
3. `source-audio-passthrough` — the exported file carries the original audio stream, unmutated, sample-accurate to the source duration. Only one reviewed route documents an explicit preserved-audio mode (Kling `settings.audio = "original"`). `V` for that operation's documented behavior, `U` for end-to-end sync quality.
4. `duration-preservation` — output duration equals source duration. Documented deviations exist: Seedance 2.5 edit output may be up to about 0.4 seconds shorter, Volcengine LAS edit output is bounded to 4–15 seconds, Gemini Omni Flash edit input is capped at 15 seconds. `U` as a guaranteed property; enforceable server-side by trim/pad during muxing.
5. `frame-rate-preservation` — output frame rate equals source frame rate. Two reviewed routes document 24 fps output regardless of input; Runway video-to-video documents no frame-rate parameter. `U` on every route. Server-side conforming is required for this rule to be satisfiable at all.
6. `aspect-ratio-preservation` — source aspect ratio is retained. Kling (`adaptive` inherits `base_video`), Seedance edit/extend (`adaptive` only), Volcengine LAS (original ratio default), and Runway Aleph (source ratio retained unless `targetAspectRatio` is used for expand/outpaint) all document ratio inheritance. `V` for documented behavior.
7. `non-destructive` — the source upload is immutable. Every result is a new derivative asset linked to its parent. No provider call may overwrite a source asset or its stored file row. `V` as a repository invariant: output publishing is already private-storage-append-only (`apps/api/Assets/GeneratedAssetPublisher.cs`, `apps/api/Files/FileStorageService.cs` boundary).
8. `temporal-continuity` — no flicker, no cut insertion, no drift across the clip. No reviewed provider API documents a temporal-continuity guarantee or exposes a flicker metric. `U`; must be measured in the trial stage with a fixture-based score, not accepted from vendor copy.
9. `private-by-default` — source and derivative footage stay inside the owner's workspace. Provider media references must be server-resolved (private storage or ephemeral upload), never browser-supplied arbitrary URLs. `V` as an existing repository invariant (`IProviderUrlPolicy` is already applied by `apps/api/Movies/RunwayMovieVideoProvider.cs` submissions, and R2 storage is a private bucket with no presigned browser uploads per `docs/FILES_ARCHITECTURE.md`).
10. `cost-controlled` — no request leaves Taslim without a prior estimate, an enforced ceiling, and an idempotent reservation. `U` for provider refund behavior on failed/cancelled work except where the vendor documents it (Runway documents refund only for `PENDING`/`THROTTLED` observed at cancellation; Volcengine documents that failed generations are not charged and that each retry is billed as a new task).
11. `honest-UX` — the UI names the transformation that a verified route actually performs, shows before/after, and never presents an unverified guarantee as a product promise. This rule is what the regression test in `apps/web/src/movie/videoMakeoverCapabilityDoc.test.ts` protects.

## Source preservation architecture

Preservation must not depend on provider goodwill. The spike's central architectural conclusion is that identity, speech, timing, and audio guarantees belong to a Taslim-owned media pipeline, while providers are used only for the visual edit.

### Provider-neutral seams that already exist

| Seam | Path | Role for this feature |
| --- | --- | --- |
| Capability registry (kinds, feature keys, constraints, cost hooks, provenance) | `apps/api/Movies/ProviderCapabilityRegistry.cs` | Server-side only. Today's kinds are `video-generation`, `video-upscaling`, `video-mastering`. It has no editing kind, and no feature key for edit-in-place, preserved source audio, or wardrobe. |
| Adapter contract (capabilities, submit, poll, callback, cancel, artifact, usage) | `apps/api/Video/VideoGenerationContracts.cs`, `docs/VIDEO_GENERATION_ADAPTER_CONTRACT.md` | Declares `video-to-video` as an operation. Disabled by default; production registration is `UnavailableVideoGenerationAdapter`. |
| Direct provider foundation (normalizers, error taxonomy, mock adapter) | `apps/api/Movies/DirectVideoProviderFoundation.cs`, `docs/DIRECT_VIDEO_PROVIDER_ADAPTER_FOUNDATION.md` | `UnsupportedCapability` error category already exists, which is the correct fail-closed response to an unverified capability. |
| Legacy movie provider interface | `apps/api/Movies/MovieEntities.cs` (`IMovieVideoProvider`) | Supports only `quick_movie` and `scene_clip` operations today. This spike does not extend it; the file is owned by open PR #269. |
| Durable jobs, fencing, replay | `apps/api/Generation/GenerationJobExecution.cs`, `docs/PROVIDER_RESILIENCE_ARCHITECTURE.md` | Single finalization claim per job/idempotency key with replay fencing; `RateLimited` is a normalized category. |
| Cost guardrails | `docs/PROVIDER_COST_GUARDRAILS_ARCHITECTURE.md` | Estimate-or-reject (`RejectUnknownEstimates` default true), per-job/user/project/workspace ceilings, exactly-once usage ledger. |
| Private publishing | `docs/FILES_ARCHITECTURE.md`, `docs/ASSET_ARCHITECTURE.md` | Server-to-server R2/S3 writes, no public URLs, copy provider output before job completion. |

### Proposed seams (to be implemented in their own PRs, not here)

1. Capability vocabulary expansion: an editing capability kind plus feature keys `edit-in-place`, `region-window-edit`, `keyframe-guided-edit`, `source-audio-passthrough`, `identity-preserve`, `wardrobe-transform`, and `frame-rate-conform`. Each registry definition must carry provenance (source URL, captured date, evidence label) so that an unverified capability cannot be routed.
2. A makeover job type separate from generation: source asset in, derivative asset out, with explicit parent linkage so that discard is trivial and the source is never mutated.
3. A deterministic media preparation/composition stage (ffmpeg, server-side, no AI) that performs: duration and fps probing of the source, chunking to route limits, output conforming to source fps and aspect, original audio stream extraction, and audio/video mux for the derivative. This stage is the only mechanism by which `duration-preservation`, `frame-rate-preservation`, and `source-audio-passthrough` can be guaranteed as product behavior.
4. A measurement stage: lip-sync offset estimation, subject embedding similarity against the source, and inter-frame flicker scoring. Until a route passes these with a real fixture, it stays `U` and stays disabled.
5. Preset registry for studio looks (name, keyframe references, prompt template, target route capability keys, cost snapshot). Presets are data, not code paths.

### Deterministic fallback inside the honest envelope

A non-AI, fully verifiable subset is achievable with the media preparation stage alone: color-grade and LUT-based grade presets, exposure/contrast normalization, and audio loudness normalization. These satisfy `source-audio-passthrough`, `duration-preservation`, `frame-rate-preservation`, and `identity-preservation` trivially because pixels other than tone are untouched. They do **not** satisfy studio swap or relight, and must not be labeled as if they did. This is the only feature that could ship before any provider gate, and it is deliberately excluded from Phase A's promises.

## Feature capability matrix

| Feature | Best documented route | Evidence | Verified guarantee | Gate before activation |
| --- | --- | --- | --- | --- |
| `studio-swap` (background/room replacement) | Kling 4.0 omni edit (`base_video` + prompt) or Volcengine LAS `scene_replace` | `V` / `V` | Background replacement is documented; subject retention is documented only as an intent, not a guarantee. `U` | Edge/hair/hand continuity fixtures; identity similarity measurement; per-route cost snapshot |
| `ai-relight` | Runway Aleph 2.0 element/frame edit; Gemini Omni Flash "change the lighting" edit example | `V` (edit operation exists) / `C` (quality) | No provider documents a lighting-only operation or shadow/reflection correctness. `U` | A relight-specific fixture set (shadow direction, skin rendering, flicker score) before any UI label |
| `video-restyle` | Kling 4.0 omni edit; Gemini Omni Flash style edit; Luma Modify style transfer | `V` / `V` / `V` | Restyle operation is documented; identity and temporal continuity are not guaranteed. `U` | Same fixture gate as relight, plus multi-shot continuity test |
| `talking-upgrade` (talking-head grade + studio look) | Composed route: edit for visuals + deterministic mux for original audio | `U` | This exact combination is documented by no provider. It is a Taslim composition, not a feature claim. | `speech-preservation` and `lip-sync` measurement must pass on real talking-head fixtures |
| `wardrobe-transform` (outfit change) | Runway Aleph 2.0 element edit, where the vendor product page names "a piece of clothing" as an element example; the BytePlus Seedance 2.5 tutorial shows an edit example, "Change the jacket to red leather" | `C` (vendor product claim) plus a documented example, neither verified as API support | Neither documents identity, hand/pose interaction, or clothing-detail persistence across frames. `U` | Phase B only. Consent check, identity similarity gate, hand/gesture review, and a wardrobe-specific fixture suite |
| `before-after-preview` | Deterministic Taslim feature (no provider) | `V` (as a repo capability) | Not a provider capability; requires only derivative linkage. | Preview resolution/rate must be enforced by the cost guardrail before full generation |
| `low-res-preview-first` | Seedance 2.5 `draft: true` preview then enhance; Runway routes the preview model | `V` (Runway Dev hosts the documented draft/enhance flow) | Preview is billed at 480p rate and enhanced separately, so the estimate must cover both charges. | Cost model must treat preview+enhance as two attempts of one job |

## Per-route verified capability and unknowns matrix

| Route | Documented editing operation | Documented constraints | Source audio and frame rate | Evidence and source |
| --- | --- | --- | --- | --- |
| Runway Dev `POST /v1/video_to_video`, model `aleph2` | `V` In-context video edit with `promptText` and up to 5 timed keyframes; each keyframe may carry an edit window (`range.start_seconds`/`end_seconds`), which is the only documented region-window primitive among reviewed routes. | `V` Input video must be 30 seconds or shorter; HTTPS/`runway://`/base64 data URI up to 5 MB; `seed`; `targetAspectRatio` is for expand/outpaint only; `contentModeration.publicFigureThreshold` (`auto`/`low`) exists and must stay `auto`; output `mp4`/`prores`/`png_sequence`/`sdr_rec709_10bit`. Edit uses the source video's length as the output duration. | `U` The endpoint documents no audio parameter and no output frame-rate parameter. Product page claims unchanged elements are preserved; that is `C`, not `V`. | `V` https://docs.dev.runwayml.com/api/ ; https://docs.dev.runwayml.com/guides/pricing/ ; https://runwayml.com/product/aleph-2 |
| Runway Dev `video_to_video`, hosted third-party models (`seedance2_5`, `kling_4`, `klingO3_*`, `gemini_omni_flash`, `gemini_omni_flash_1.1`, `hailuo3`, `seedance2*`) | `V` Accepted model values are documented on the same endpoint, so aggregator routing is real. Model-specific edit modes are documented only for `seedance2_5`, whose `mode` accepts the values reference, extend, and edit. | `V` Per-model rates are published in Runway Dev credits (`$0.01`/credit portal rate). Rates for hosted models are Runway's, not the origin vendor's, and may differ from direct pricing. | `U` Whether each hosted route exposes the origin vendor's audio-preservation switch through Runway is undocumented; `aleph2`-only parameter shapes are shown. | `V` https://docs.dev.runwayml.com/guides/models/ ; https://docs.dev.runwayml.com/guides/pricing/ |
| Kling official `POST /omni-video/kling-4.0` (and Kling 4.0 Flash) | `V` The `contents` array supports a `base_video` entry (the video to be edited, at most one) plus `feature_video`, `refer_image`, `element`, `voice`, and `prompt` entries that are cross-referenced by `@id`. Editing is prompt-driven, including style/subject/background changes. | `V` Video `.mp4`/`.mov`, at most 300 MB, each and total video duration 3–30 s, input 24–60 fps, generated video 24 fps, resolution 720p/1080p, duration 3–30 s, `aspect_ratio: adaptive` inherits `base_video`, at most 15 combined references. Callback protocol and concurrency rules are documented. | `V` `settings.audio: "original"` preserves the audio of the input video (uses `base_video` audio if present, else the first `feature_video`); voice references are not allowed in the same request. Frame rate `U` (24 fps output is documented, source fps retention is not). | `V` https://kling.ai/document-api/api/video/4-0/video-omni ; https://kling.ai/document-api/guides/protocols/privacy-policy |
| BytePlus ModelArk / Volcengine Ark `doubao-seedance-2-5` with `omni_reference_task_type: "edit"` | `V` Documented as editing the visuals or audio of an original video, including subject replacement, add/remove, and partial repaint, driven by prompt keywords. `extend` and `reference` are separate documented subtasks. | `V` Requires at least one `reference_video`; that video must be 4–30 s; `ratio` must be `adaptive`; `duration` must be `-1`; output duration approximately equals source (may be up to about 0.4 s shorter and may be fractional); returned duration is floor(frames/24). `mov` is recommended for edit input and output. 1080p/4K output is 10-bit H.265. | `V` for the existence of audio editing, and for mono output of generated audio (`generate_audio`; outputs are always mono). `U` whether the original speech track of the edited video is preserved verbatim; no passthrough flag is documented. `U` source fps. | `V` https://docs.byteplus.com/en/docs/ModelArk/2607688 ; https://docs.byteplus.com/en/docs/modelark/create-video-generation-task-api |
| Volcengine LAS operator `las_video_seedance_replace` (Video Edit Enhanced) | `V` `scene_replace` is documented as retaining the people, products, actions, and shot structure while replacing the background or environment style. `object_replace` replaces a target object from a reference image. `person_replace` exists but is an allowlist feature and is out of scope for this spike. | `V` Input video 2–15 s, at most 200 MB, 480p/720p/1080p, aspect 0.4–2.5, pixel area 409,600–2,086,876; 1–9 reference images; optional reference audio 2–15 s; output 4–15 s; `user_prompt` carries constraints; `enable_retry` is billed per retry; `task_id` valid 3 days; max 600 RPM and concurrency 10; outputs land in TOS. Real-person media must first enter the LAS asset library (`seedance_use_asset_api = true`, allowlist). | `U` Audio handling of the edited video is not documented (audio is listed as an input modality and removal-by-prompt is an example, which implies regeneration rather than passthrough). `U` exact fps. Duration is bounded, not preserved. | `V` https://docs.volcengine.com/docs/lake-ai-service/video-editing-enhanced-edition?lang=en |
| Google Gemini API `gemini-omni-1.2-flash` (Interactions API) | `V` Conversational, multi-turn video editing, including edits of videos uploaded through the Files API; documented edit examples include background swap, style change, and lighting change. | `V` Uploaded edit input must be 15 s or shorter; extension appends 3–10 s up to a 30 s total; reference video audio is ignored; reference videos apply only to new generation, not to editing; audio inputs and audio references are unsupported in the current API version; editing or extending uploaded videos is unavailable in the EEA, Switzerland, and the UK; recognizable-person and minor inputs are restricted; all outputs carry SynthID; no temperature/system-prompt/negative-prompt controls. | `N` Voice editing is documented as unsupported, and the model generates its own audio track by default; there is no documented source-audio passthrough. `U` fps and duration equality. | `V` https://ai.google.dev/gemini-api/docs/omni ; https://ai.google.dev/gemini-api/docs/video |
| Google Veo 3.1 (Vertex AI / Gemini Enterprise, `veo-3.1-generate-001`, `-fast-`, `-lite-`) | `N` The documented capability list is text-to-video, image-to-video, first/last frame, video extension, and reference-image input. No in-place source-video edit operation is documented. The Gemini API Veo 3.1 preview is documented as shut down on 2026-10-22. | `V` 4/6/8 s outputs, 720p/1080p in, 720p/1080p/4K out, 24 fps, `video/mp4`, `us-central1`, Provisioned Throughput and fixed quota only (no pay-as-you-go), 50 tokens/minute regional online-prediction quota, retirement dated 2026-11-17 or later. | `V` for sound generation as a model output; `U` for any preservation property, because the editing feature does not exist on this route. | `V` https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/veo/3-1-generate ; https://ai.google.dev/gemini-api/docs/video |
| Luma `POST /dream-machine/v1/generations/video/modify` (`ray-2`, `ray-flash-2`, `mode: adhere_*/flex_*/reimagine_*`) | `V` Documented as style transfer and prompt-based editing, with documented use cases for restyle/retexture, wardrobe and prop change, environment swap, and element-level edits. | `V` Legacy platform: `ray-2` up to 10 s / 100 MB, `ray-flash-2` up to 15 s / 100 MB, optional guiding `first_frame`. Luma now directs developers to the Luma Agents API; Ray3.2 Modify appears in Luma product surfaces but no public API parameter contract was verified here. | `U` Audio behavior is undocumented on this endpoint (adding audio is a separate documented operation). `U` fps and duration equality. | `V` https://docs.lumalabs.ai/reference/modifyvideo ; https://docs.lumalabs.ai/docs/modify-video ; `U` https://lumalabs.ai/api |
| fal.ai / Replicate aggregator routes | `U` Model-specific routes exist, but capability switches (audio passthrough, edit windows) do not transfer reliably from the origin vendor and were not verified per endpoint for this spike. | `U` Route-specific limits and terms must be re-verified per model endpoint. Keep these as benchmark/fallback only, labelled with the origin model, per `docs/PROVIDER_READINESS.md`. | `U` | `U` https://docs.dev.runwayml.com/guides/models/ |

No reviewed route is verified for `identity-preservation`, `speech-preservation`, `temporal-continuity`, or `frame-rate-preservation`. Those are the four properties that the issue's acceptance criteria depend on, which is why the phased matrix below gates them behind measurement rather than behind integration.

## Cost, protection, and privacy gates

### Estimate and reservation

1. Estimate before queue. Use `GenerationCostEstimator` semantics already documented in `docs/PROVIDER_COST_GUARDRAILS_ARCHITECTURE.md`: unknown estimate must reject (`RejectUnknownEstimates` default `true`), never zero.
2. Reservation is idempotent. One `generation:{jobId}` usage key per job; retries and fallbacks add provider-attempt audit rows only, per the existing exactly-once ledger rule.
3. Preview and enhance are two charges. Seedance 2.5 `draft: true` preview bills at the 480p rate and the later 1080p enhance bills again; the estimate must sum both when the user opts into a preview-then-keep flow. `V` https://docs.dev.runwayml.com/guides/pricing/
4. Input video is a billed dimension. `V` Runway Dev bills video-to-video against input duration (Seedance 2.5 adds per-second input/reference charges on top of output; Kling 4.0 bills the greater of total input and output duration), so a "makeover of a long clip" cost grows linearly with source length. `V` Volcengine bills `(input + output) / 2` seconds multiplied by a resolution coefficient. Long sources must therefore be chunked by the media preparation stage, and chunk count must appear in the estimate.
5. Published snapshots (credits at `$0.01`/credit Runway Dev portal rate, as captured on 2026-11-12; derived arithmetic, not vendor quotes):
   - Aleph 2.0: 28 credits per second, 56 credit minimum per generation. A 10 s edit derives to about 280 credits.
   - Seedance 2.5 through Runway Dev: 68 credits/s output at 1080p plus 34 credits/s input/reference video, 80 credit minimum. A 10 s in / 10 s out 1080p edit derives to about 1,020 credits, an order of magnitude above Aleph.
   - Kling O3 video-to-video through Runway Dev: billed per second of input video (13 / 17 / 43 credits per second for standard / pro / 4K).
   - Gemini Omni Flash video-to-video through Runway Dev: 11 credits per second of input video, capped at 10 seconds, plus 1 credit per reference image.
   - Volcengine LAS Video Edit Enhanced: CNY per second of `(input + output) / 2` with resolution coefficients; the documented example works out to 36.48 CNY for a 5 s in / 7 s out 1080p task. Per-second unit prices for Seedance 2.5 on this operator were not captured in full here.
   - Kling and Veo direct-API unit prices: `U` from the pages reviewed for this spike.
6. Kill switch and ceilings remain server-side. Per-job, per-user, per-project, and workspace ceilings, plus a global disable, must all be evaluated before any route is enabled.

### Provider protection semantics

| Concern | Verified behavior | Consequence for design |
| --- | --- | --- |
| Refund on cancellation | `V` Runway Dev: refund depends on the status the server observed at cancellation time; `RUNNING` does not refund, and cancellation does not guarantee generation stops. Deleting a completed task does not refund. | Do not model cancellation as a credit refund. Reserve, then settle from attempts. |
| Failed generations | `V` Volcengine LAS: tasks that fail review or operation do not incur fees. `U` for Runway, Kling, BytePlus, Google, Luma. | Estimate conservatively; reconcile from provider usage evidence. |
| Retry billing | `V` Volcengine: enabling `enable_retry` bills each retry as a new task. | Retries must go through the same ceiling check, not around it. |
| Output retrieval window | `V` Runway Dev: canceled/deleted tasks cannot be fetched again; copy output into Taslim private storage before completion. `V` Volcengine: `task_id` results are retrievable for 3 days. | Copy-on-success is mandatory; late re-fetch is not a recovery strategy. |
| Polling discipline | `V` Runway Dev documents that task updates should not be expected more often than every five seconds. Existing `MovieVideoOptions` default `StatusPollIntervalSeconds = 5` already matches. | Reuse existing bounded polling; do not add per-feature poll tunables. |
| 429 handling | `V` Runway Dev documents `429`; Kling documents concurrency rules and callback retry behavior; Volcengine documents 600 RPM and concurrency 10. | Map to the existing `RateLimited` category; back off and requeue without a second attempt charge. |
| Idempotent submission | `V` Kling accepts an `external_id` per task. Runway Dev identifies tasks by server-generated UUID. | Persist a client request id where the provider supports one; otherwise fence with the finalization claim. |

### Privacy, consent, and access gates

1. Storage and access: sources and derivatives remain in private R2/S3 with server-side resolution only, per `docs/FILES_ARCHITECTURE.md`; asset readback is workspace-scoped (covered by the open guards in PRs #727 and #725 lineage).
2. No free public AI calls: the launch fence is documented in `docs/ops/public-free-tier-provider-spend-firewall.md` (owned by open PR #699). This spike does not modify or depend on its contents, and does not claim the gate is complete.
3. Authorization: movie authorization and workspace membership checks must be exercised by the fail-closed tests in the open lineage (#717, #719, #722, #727).
4. Likeness and consent: recognizable-person inputs are restricted on the Google route (`V`), public-figure moderation is configurable on Runway and must stay at `auto` (`V` for the parameter, `U` for policy adequacy), and Volcengine requires real-person media to pass through its allowlist asset library first (`V`). None of this is a consent system. Taslim-side consent capture, provenance recording, and an audit trail remain unverified requirements before any wardrobe or identity-adjacent feature ships.
5. Region constraints: Google's upload-edit route is unavailable in the EEA, Switzerland, and the UK (`V`). Any route table must therefore carry a region availability field, and studio presets that depend on that route must degrade with an honest message rather than silently falling back.
6. Watermark/provenance: Google Omni outputs carry SynthID (`V`). Taslim must retain the provider, model, prompt, keyframe references, seed, cost snapshot, and output hash with the derivative, per the continuity rule already documented in `docs/PROVIDER_READINESS.md`.

## Phased implementation matrix

| Phase | Scope | Owned seams | Provider evidence required to enter | Exit criteria |
| --- | --- | --- | --- | --- |
| 0 | This spike: capability documentation plus its regression test | `docs/movie-video-makeover-capabilities.md`, `apps/web/src/movie/videoMakeoverCapabilityDoc.test.ts` | Public official documentation only (this document) | Documentation merged as a reviewable artifact; no flag changes; ownership conflict check recorded |
| 1 | Capability vocabulary: editing kind and feature keys with mandatory provenance and fail-closed validation | `apps/api/Movies/ProviderCapabilityRegistry.cs` (unowned by any open PR at the time of writing) | None | Registry rejects any edit-capability definition lacking source URL, captured date, and evidence label |
| 2 | Deterministic media preparation and composition stage (probe, chunk, conform, mux, loudness) | New module, no edits to `MovieFinalAssembly.cs` (owned by open PRs #298/#305) or `MovieEntities.cs` (owned by open PR #269) | None | Fixtures prove duration/fps/aspect/audio-stream identity preservation for a passthrough-only path |
| 3 | Measurement gates: lip-sync offset, identity similarity, flicker score | New module | A single approved paid fixture run per candidate route, with explicit user approval | Every route used in Phase 4 has a recorded score set; unmeasured routes remain unroutable |
| 4 | Studio Swap and Video Restyle behind flags, preview then full render | Makeover job type plus the existing adapter contract (`VideoGenerationAdapters:Enabled` stays false) | At least one route with `V` editing plus a documented preserved-audio path or a verified mux strategy | Private beta allowlist only; cost estimate shown and confirmed; cancellation refund semantics documented honestly |
| 5 | AI Relight as a labeled preset family | Same as Phase 4 | Relight fixture pass | No "relight" label on any route that has not passed the relight fixture |
| 6 | Talking Video Upgrade (composite: visual edit plus original audio) | Phase 2 plus Phase 3 results | `speech-preservation` pass on talking-head fixtures | Ship only if measured sync drift is within the accepted threshold; otherwise expose as grade-only |
| 7 | Optional Wardrobe Transform | Phase 4 plus consent capture and provenance audit | `wardrobe-transform` pass plus legal/terms review for likeness | Opt-in, allowlisted, never on by default; region and consent gates enforced |
| 8 | Multi-shot consistency, saved presets, masking, precise cleanup | Preset registry plus region-window editing (`V` on Runway Aleph keyframe ranges only) | Route-specific verification per capability | Not scheduled ahead of its evidence; each sub-feature needs its own `V` |

### Explicitly out of scope for this spike

1. No provider or model activation, no credential, no paid call, no entitlement test, no rate-limit probe.
2. No edits to `apps/web/src/components/MovieStudioView.tsx` or its test (open PRs #762, #580, #269 own that surface), to `apps/api/Movies/MovieEntities.cs` (open PR #269), or to `apps/api/Movies/MovieFinalAssembly.cs` (open PRs #298, #305).
3. No routing, UI, queue, billing, or storage behavior change. Nothing here enables a feature.
4. No face swap, person substitution, or voice cloning in any phase. Volcengine `person_replace`, Kling voice customization, and Runway `publicFigureThreshold: "low"` are rejected by design, not deferred.
5. No merge authority. Production deployment remains gated on the issue's own release-gate language and on owner approval.

## Verification follow-ups

Open unknowns, each with the specific test that converts it to a verified fact. Until then the affected feature stays disabled.

| Unknown | How to verify | Blocking |
| --- | --- | --- |
| Does Runway Aleph 2.0 keep the source audio track, and at what frame rate? | Controlled single-shot test on one 10 s fixture with speech; ffprobe the returned file for audio stream and fps; compare hashes to the source audio extraction. | Talking Video Upgrade, any audio-bearing claim |
| Does Kling `settings.audio = "original"` remain lip-synced with the edited visuals? | Kling 4.0 omni edit on the same fixture; measure lip-sync offset against the source. | Studio Swap and Restyle on talking heads |
| Does Seedance 2.5 edit preserve the original speech track or regenerate audio? | ModelArk edit task with `generate_audio` variants; ffprobe plus transcription comparison. | Restyle route selection |
| Are hosted third-party edit models on Runway Dev feature-complete versus the vendor API (for example Kling audio mode, Seedance edit mode)? | Compare parameter surfaces in the Runway Dev reference against the vendor reference for each model actually routed. | Any aggregator-priority decision |
| Is Luma Ray3.2 Modify available through a stable public API, with what limits and what audio behavior? | Review the Luma Agents API contract when published; legacy `dream-machine` modify limits do not answer this. | Optional benchmark only |
| Does Volcengine LAS `scene_replace` keep subject edges, hands, and props within tolerance? | Region-window fixture scoring with the Phase 3 measurement stage. | Studio Swap on the `scene_replace` route |
| What are the direct API unit prices and retention windows for Kling and Veo routes? | Vendor pricing pages and vendor confirmation. | Cost estimate coverage |
| Does any reviewed provider document a temporal-continuity or flicker guarantee? | None found as of 2026-11-12. Ask each vendor; treat silence as `U` forever unless answered. | `temporal-continuity` acceptance rule |

## File ownership check

Performed on 2026-11-12 against all 504 open pull requests in this repository by comparing each PR's base-to-head file list.

1. `docs/movie-video-makeover-capabilities.md`: not added, renamed, or referenced by any open PR. This path is new.
2. `apps/web/src/movie/`: not present on `main` and touched by no open PR. This spike's test lives there to stay clear of the crowded `apps/web/src/components` and `apps/api.Tests` surfaces.
3. Owner PRs confirmed open and untouched by this change: #269 (`apps/api/Movies/MovieEntities.cs`, `MovieStudioView.tsx` and its test, two `MovieFinalAssembly` test files), #263 (measured output duration readiness), #298 (`apps/api/Movies/MovieFinalAssembly.cs` plus its tests, based on the movie take-select language branch).
4. Other open PRs touching `MovieStudioView.tsx`/`MovieStudioView.test.tsx`: #762, #580. Neither is modified here.
5. `apps/api/Movies/ProviderCapabilityRegistry.cs`: touched by no open PR; deliberately left unmodified in this spike and proposed as Phase 1's own narrow change.

## Sources consulted

1. Runway Dev API reference — video-to-video endpoint, parameters, task cancellation/refund semantics. https://docs.dev.runwayml.com/api/
2. Runway Dev available models guide, including Seedance 2.5 `mode: edit` and draft/enhance flow. https://docs.dev.runwayml.com/guides/models/
3. Runway Dev API pricing, including per-second video-to-video and input-video billing. https://docs.dev.runwayml.com/guides/pricing/
4. Runway Aleph 2.0 product page (vendor claim, labelled `C`). https://runwayml.com/product/aleph-2
5. Kling API documentation, Kling 4.0 Omni Video Generation, including `base_video` and `settings.audio: "original"`. https://kling.ai/document-api/api/video/4-0/video-omni
6. Kling API service privacy policy. https://kling.ai/document-api/guides/protocols/privacy-policy
7. BytePlus ModelArk Dreamina Seedance 2.5 tutorial, task types and editing constraints. https://docs.byteplus.com/en/docs/ModelArk/2607688
8. BytePlus ModelArk create video generation task API reference. https://docs.byteplus.com/en/docs/modelark/create-video-generation-task-api
9. Volcengine Lake AI Service Video Edit Enhanced operator (`las_video_seedance_replace`, `scene_replace`). https://docs.volcengine.com/docs/lake-ai-service/video-editing-enhanced-edition?lang=en
10. Google Gemini API video generation guidance and Omni editing documentation. https://ai.google.dev/gemini-api/docs/video and https://ai.google.dev/gemini-api/docs/omni
11. Google Cloud Gemini Enterprise Veo 3.1 capability reference. https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/veo/3-1-generate
12. Luma Modify Video API reference and guide. https://docs.lumalabs.ai/reference/modifyvideo and https://docs.lumalabs.ai/docs/modify-video

Repository seams referenced: `docs/PROVIDER_READINESS.md`, `docs/PROVIDER_CAPABILITY_REGISTRY.md`, `docs/VIDEO_GENERATION_ADAPTER_CONTRACT.md`, `docs/DIRECT_VIDEO_PROVIDER_ADAPTER_FOUNDATION.md`, `docs/PROVIDER_COST_GUARDRAILS_ARCHITECTURE.md`, `docs/PROVIDER_RESILIENCE_ARCHITECTURE.md`, `docs/MOVIE_STUDIO_ARCHITECTURE.md`, `docs/UPSCALING_ARCHITECTURE.md`, `docs/FILES_ARCHITECTURE.md`.
