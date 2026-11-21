# Release Evidence — Authorized Workspace-Scoped Music Reference Assets (PR #805)

| Field | Value |
| --- | --- |
| Slice | Server-side asset/storage authorization path for Music Studio reference media (issue #708, acceptance criterion 3) |
| Pull request | [#805](https://github.com/rebin2002/TaslimAI/pull/805) — `feat/music`: resolve reference assets into authorized workspace-scoped evidence |
| Lane branch | `feat/music-reference-asset-authorization-20261114` |
| Base | `main` |
| Evidence prepared against | `main` = `da8d8b55be1ed3058c986ee71797a1caa03b17b3` (2026-11-21T18:23:12Z, merge of #1222) |
| Code candidate (release subject) | `e98ce379167ed12a300577c5b2b0b2e52236d5ac` |
| Evidence generated | 2026-11-21 (UTC), read-only inspection + local reproduction; nothing merged, deployed, closed or reconfigured |
| Gate posture | Label `security-review-required`; `reviewDecision` empty — independent security sign-off is still outstanding and is **not** supplied by this document |

This file is documentation for the lane. It changes no code: the two source files certified here are byte-identical to the tree at `e98ce379`.

---

## 1. Provenance

**Commit lineage.** Two commits sit between current `main` and the candidate; the candidate is not an ancestor of `main` (the lane is unmerged).

| Position | SHA | Date (UTC) | Content |
| --- | --- | --- | --- |
| merge-base with `main` | `c7622a13142ba85c3009955fbfafb2af0469f903` | 2026-10-27 | `fix(files): set explicit S3 Content-Length for user attachments (#637)` |
| commit 1 | `c97e5890` | 2026-11-14T02:22:28Z | `feat(music): resolve reference assets into authorized workspace-scoped evidence (#708 slice)` |
| commit 2 = head | `e98ce379` | 2026-11-21T18:20:58Z | `fix(music): refuse expired and deletion-recorded references at the access layer (#708, #1152)` |

**Lane scope.** `git diff --name-status` from the merge-base to the head is exactly two added files, 793 insertions, zero deletions, zero modifications:

- `apps/api/Music/MusicReferenceAssetAuthorization.cs` (311 lines)
- `apps/api.Tests/MusicReferenceAssetAuthorizationTests.cs` (482 lines)

**Integration with current main was actually tested, not assumed.** The exact-head CI ran on the pull-request merge ref `7e10768438463d747765bda4259c2eca3bd94fae`, whose two parents are `da8d8b55` (the current `main` tip) and `e98ce379` (the lane head). Every check below therefore executed the candidate merged onto current main.

**Machine-generated release identity.** Workflow `Taslim Release Evidence`, run `44935259311`, job `136940367856`, artifact `taslim-release-evidence-44935259311` (ID `14719686278`), manifest generated `2026-11-21T18:23:36Z`:

| Field | Value |
| --- | --- |
| `source.commitSha` | `7e10768438463d747765bda4259c2eca3bd94fae` |
| `source.ref` | `refs/pull/805/merge` |
| `buildInputs.fingerprintSha256` | `8fe5df4c4b59f04ebc57290781ad050cfe4f999a9f6ef24631c4abcbabaec9e6` |
| `migrations.fingerprintSha256` | `13cdf6061119db16a44d902b6ea720e8031f6b6d4f2901557111ff07c2aef642` |
| migrations | 82 files, chain head `20261005112000_AddImageActiveJobGuard.cs` |
| `productionSafetyDefaults` | `databaseApplyMigrations: true`, `customerChargingEnabled: false`, `openAiEnabled: false`, `persistentStorageProvider: S3Compatible` |
| base images | 5, digest-pinned |

Per `docs/RELEASE_EVIDENCE.md`, a `pull_request` run hashes the merge ref, which is reachable from no branch and is garbage-collected when the PR is updated or closed: **this manifest identifies the merge candidate, not a production SHA.** The authoritative release record remains the `push` run that follows a merge. What makes it usable here is that the fingerprints match current main's own push evidence (run `44935242805`, source `da8d8b55`) exactly:

- `buildInputs.fingerprintSha256` — identical (`8fe5df4c…`)
- `migrations.fingerprintSha256` — identical (`13cdf606…`), file-for-file across all 82 migrations
- every hashed build input identical: `package-lock.json`, `apps/api/Taslim.Api.csproj`, `apps/api/Dockerfile`, `apps/web/Dockerfile`, `apps/api/appsettings.Production.json`
- `productionSafetyDefaults` and base-image digests identical

**Reproduced off-CI.** Running `bash scripts/release-evidence.sh` against a clean checkout of the merge ref locally reproduced the same `buildInputs` and `migrations` fingerprints, the same manifest-sha inputs and the same safety defaults. The only differences were the environment-identity fields the generator reads from CI (`GITHUB_REF`, `GITHUB_REPOSITORY`, `GITHUB_RUN_ID`, and the declared node/dotnet versions) — no hashed input drifted. The evidence is therefore reproducible by any reviewer from the revision it names.

**Rollback posture.** Migration fingerprint unchanged from main ⇒ the lane neither advances nor reorders the schema chain; a deployment carrying this candidate has the same database posture as one that does not, so no migration review is triggered by merging it.

---

## 2. Ownership

**Claimed surface.** The lane owns exactly the two paths in §1. Neither exists on current `main` (`git ls-tree origin/main -- apps/api/Music/MusicReferenceAssetAuthorization.cs` → empty), and none of the seven types it introduces appears anywhere on main (0 files each for `MusicReferenceAssetAuthorizationCodes`, `MusicReferenceAssetSelection`, `MusicReferenceAuthorizedAsset`, `MusicReferenceAuthorizationDenial`, `MusicReferenceAssetAuthorizationOutcome`, `IMusicReferenceAssetAuthorizer`, `MusicReferenceAssetAuthorizer`) — so integration onto current main creates no duplicate-type conflict.

**Cross-lane scan.** All 63 open PRs whose title or branch name matches `music|asset|storage|reference|upload|provider|media` were file-listed. Only four contain the lane's paths:

| PR | State | Relationship to these files |
| --- | --- | --- |
| #805 | open | **Producer.** Sole author; `e98ce379` exists on no other ref. |
| #815 | open | Consumer stack: `read-only reference eligibility API joining #805 and #776`. Carries the lane's files **as of `c97e5890`** through `integration/music-reference-evidence-base-20261114`. |
| #1116 | open | Test-only HTTP QA of #815's join; carries the same stale `c97e5890` copy. |
| #1152 | open | Test-only retention/malformed QA of #815's join; carries the same stale `c97e5890` copy. |

**Staleness quantified.** #815's copy of the producer is 37 lines shorter and its copy of the tests is 142 lines shorter than the candidate; it contains **zero** references to `RetainUntil`/`DeletedAt` (candidate: 5) and 18 test attributes (candidate: 27). The hardening in `e98ce379` exists only in this lane. #815/#1116/#1152 must be re-cut onto this lane's head; that refresh belongs to those lanes, not here.

**Boundaries honoured (verified on current main, not asserted).**

- `MusicGenerationController.cs` — untouched (owned by #359).
- `MusicGenerationContracts.cs`, `MusicGenerationRequestValidator.cs`, `MusicGenerationCapabilityCatalog.cs` — untouched (owned by #709's lane; the validator and catalog already live on main).
- `MusicReferenceEligibilityPolicy.cs` — untouched, and **not imported**: the lane defines no type that shadows main's `MusicReferenceAssetAccessEvidence` / `MusicReferenceMediaProbeEvidence`, it only maps onto them.
- `Program.cs` — untouched (DI is #815's file; see §4).
- Upload/storage services, file storage, provider adapters — untouched.
- Reference-video byte inspection — not performed here: `ContainerFormat`, `DurationSeconds` and `AudioTrackDetected` stay null/unknown for video so that #774 remains the single place those facts are established.
- `docs/MUSIC_STUDIO_LOG_PRIVACY.md` on main already records `MusicReferenceAssetAuthorization*` as belonging to this reference lane group, so the log-privacy exclusion is consistent with this lane, and this lane adds no logging of asset content.

---

## 3. API authorization

Issue #708 criterion 3 requires that the backend associate an authorized workspace asset id and that the server validate access. On current main that step does not exist: a reference asset id can travel from the browser to the provider gate without any server code confirming the asset exists, is in the requesting workspace, is active, has a ready stored file with consistent lineage, or is the media kind the client claimed. This lane supplies that resolution step and nothing else.

**Decisions taken at the access layer, in order** (each verified in the source at `e98ce379`):

| # | Check | Denial code | Evidence used |
| --- | --- | --- | --- |
| 1 | authenticated principal and workspace present | `MUSIC_REFERENCE_AUTH_PRINCIPAL_INVALID` | server session |
| 2 | selection list non-empty | `MUSIC_REFERENCE_AUTH_SELECTIONS_MISSING` | request shape |
| 3 | selection list within the structural bound (`MaxLookupsPerRequest = 10`) | `MUSIC_REFERENCE_AUTH_SELECTION_LIMIT` | request shape |
| 4 | media kind is `audio`/`video` (trim + lowercase is the only client courtesy) and id is a non-empty GUID | `MUSIC_REFERENCE_AUTH_SELECTION_INVALID` | request shape, **before any lookup** |
| 5 | no duplicated id | `MUSIC_REFERENCE_AUTH_SELECTION_DUPLICATED` | one lookup per distinct id |
| 6 | asset resolvable to this user | `MUSIC_REFERENCE_AUTH_ASSET_NOT_AVAILABLE` | `IAssetService.GetAsync` |
| 7 | asset workspace equals request workspace | `MUSIC_REFERENCE_AUTH_WORKSPACE_MISMATCH` | stored asset row |
| 8 | asset active | `MUSIC_REFERENCE_AUTH_ASSET_ARCHIVED` | stored asset row |
| 9 | stored file present, `Ready`, non-zero size | `MUSIC_REFERENCE_AUTH_FILE_UNAVAILABLE` | stored file row |
| 10 | deletion recorded (`DeletedAt` set) | `MUSIC_REFERENCE_AUTH_FILE_DELETED` | stored file row |
| 11 | retention elapsed (`RetainUntil <= now`, inclusive) | `MUSIC_REFERENCE_AUTH_FILE_EXPIRED` | stored file row + server clock |
| 12 | file workspace equals asset workspace | `MUSIC_REFERENCE_AUTH_FILE_LINEAGE_MISMATCH` | stored file row |
| 13 | requested kind agrees with asset MIME **and** stored-file content type | `MUSIC_REFERENCE_AUTH_MEDIA_KIND_MISMATCH` | both recorded MIME values |

**Why the ownership rule cannot be weakened.** Step 6 delegates to `IAssetService.GetAsync`, which on current main (`apps/api/Assets/AssetService.cs` lines 39–42, 154–162) applies `AssetVisibility.ForWorkspaceMember(userId)` and then `WorkspaceAccessService.IsMemberAsync`. That is the same expression the asset library and every other studio use, so this lane cannot invent a looser rule, and unknown ids, foreign personal assets and visibility-refused assets all answer with one code and one message — an identifier is treated as an address, never as a grant.

**Ordering matters for disclosure.** The ownership fence (6) and the workspace fence (7) run before the lifecycle checks (10)–(11), so a requester outside the fence cannot learn that another workspace's row is expired or deleted; they receive the uniform "not in your accessible library" answer.

**Only server-recorded facts are reported.** `ContentType`, `SizeBytes`, `ContainerFormat` and `DurationSeconds` come from the stored-file row written by server-side file processing; "never probed" is reported as null, never guessed. `AudioTrackDetected` is `true` only for recorded audio kinds and stays null for video until a real probe (#774) records it. The requested kind must agree with both recorded MIME values; any disagreement, or any value the server cannot classify, denies.

**Fail-closed, aggregated, leak-free.** `AllAuthorized` is true only when at least one selection was submitted and nothing denied; partial success is reported with `AllAuthorized: false` and every failing selection listed in one pass. No storage key, stored or original file name, path, URL or provider detail appears in any record, and denial messages are fixed text carrying no identifier. The lane pins this contractually: a sentinel storage key is placed in the fake stored file and asserted absent from the serialized outcome, and the record's field names are pinned by reflection so a future leaky field fails the contract.

**Code namespace discipline.** The lane defines only `MUSIC_REFERENCE_AUTH_*` codes. It deliberately does not reuse or redefine `MUSIC_REFERENCE_*` (owned by #776's policy on main) or `MUSIC_*` (owned by the generation validator on main), which the integration-gate audit flagged as an anti-pattern, and it takes no eligibility decision (size/duration/MIME acceptability stays with #776's policy and #709's validator).

**Compatibility with current main's consumers.** `MusicReferenceAuthorizedAsset` maps field-for-field onto main's `MusicReferenceAssetAccessEvidence` (asset id, asset workspace, verification state, `VerifiedByService = "music_reference_asset_authorization"`) and onto `MusicReferenceMediaProbeEvidence` for the facts the server already recorded. The two type sets never import each other, so a later adapter can join them without either file changing.

**Not provided here, deliberately:** any HTTP surface. There is no controller, no route, no DI registration and no request/response contract in this lane.

---

## 4. Provider and cost safety

| Posture item | Current `main` | PR #805 candidate |
| --- | --- | --- |
| `MusicGeneration:Enabled` (dev + `appsettings.Production.json`) | `false` | unchanged (settings files not in the diff) |
| Provider credentials read by this lane | none | none — 0 references to `Mubert*`, `StableAudio*`, Suno, `HttpClient`, `IConfiguration`, environment variables or API keys in either lane file |
| Capability catalog `SupportsAuthorizedWorkspaceReferences` | `false` for all three profiles (`InstrumentalOnly`, `Unsupported`, `LegacyInstrumentalOnly`) | unchanged |
| Validator behaviour while that flag is false | throws `MUSIC_PROVIDER_UNAVAILABLE` — "Reference assets are not enabled for the configured provider and workspace authorization path" | unchanged |
| DI registration of `IMusicReferenceAssetAuthorizer` | absent | absent (`Program.cs` untouched; the type is referenced nowhere outside the lane's own two files) |
| Generation jobs, usage rows, credit entries | untouched | untouched |
| Migrations / schema | 82, head `20261005112000_AddImageActiveJobGuard.cs` | identical fingerprint; 0 migration files in the diff |
| `customerChargingEnabled` / `openAiEnabled` (release-evidence safety defaults) | `false` / `false` | identical |
| Rights and provider-capability determination | owned by #776's policy and #709's catalog | not claimed by this lane |

Merging this lane therefore enables nothing observable: it cannot be called, it cannot charge, and it cannot reach a provider. Song/reference generation stays gated exactly as it is on main today, and flipping `SupportsAuthorizedWorkspaceReferences` remains a separate, reviewable decision owned by #709's catalog plus the provider-capability evidence #776 requires (rights attestation, named voice right-to-use, verified provider capability).

CI-side safety of the evidence work itself: all three workflows (`Taslim CI`, `Taslim Release Evidence`, `Production container smoke`) run with `permissions: contents: read`, contain no deploy job, no registry push, no `environment:` block and no secret reference. The container job builds images and health-smokes them locally. No Railway service, variable or deployment was read or modified at any point in preparing this evidence.

---

## 5. Exact-head CI

Candidate `e98ce379167ed12a300577c5b2b0b2e52236d5ac`, event `pull_request`, merge ref `7e10768438` = current `main` `da8d8b55` + head. All seven checks completed green in the run window 2026-11-21T18:23:27Z → 18:33:33Z.

| Workflow | Check | Job | Result |
| --- | --- | --- | --- |
| Taslim CI | Release evidence and safety contract (`44935259311`) | `136940367856` | success |
| Taslim CI | Deployment smoke helper contracts | `136940368279` | success |
| Taslim CI | Frontend checks | `136940368344` | success |
| Taslim CI | Autopilot next-wave gate | `136940368032` | success |
| Taslim CI | API integration tests | `136940368277` | success — `Failed: 0, Passed: 2009, Skipped: 0, Total: 2009` |
| Taslim CI | Browser E2E | `136940938617` | success |
| Production container smoke | Build production images and smoke-test API | `136940367916` | success |

**The lane's tests demonstrably executed in CI, not just locally.** Current main's own push run (`44935242831`, API integration tests job `136940315736`) reports 1971 tests; the candidate's merge-ref run reports 2009. The delta is **+38**, exactly the number of cases in `MusicReferenceAssetAuthorizationTests` (22 `[Fact]` + 5 `[Theory]` × 16 `[InlineData]` rows across 27 methods). No pre-existing test was lost: main's 1971 are all still present and green.

**Local reproduction** (.NET SDK 8.0, worktree checked out at `e98ce379`):

| Command scope | Result |
| --- | --- |
| `--filter FullyQualifiedName~MusicReferenceAssetAuthorization` | `Passed: 38, Failed: 0` (115 ms) |
| `--filter FullyQualifiedName~Music` | `Passed: 70, Failed: 0` |
| `bash scripts/release-evidence.test.sh` | passed (including the fail-closed case where `GITHUB_SHA` disagrees with the checkout) |
| `bash scripts/release-evidence.sh` on the merge ref | fingerprints identical to CI's artifact (see §1) |

These tests need no database, no web factory, no network, no clock injection and no provider — which is itself the construction proof for §4.

**Note on the PR description.** The body of #805 predates the head commit and quotes 28 cases / 19 methods and a 60-test music filter. The certified figures at the exact head are **38 cases / 27 methods** and a **70-test** music filter; the body also still describes #776 and #709 as open prerequisites, which §6 corrects.

---

## 6. Dependency order

**Already on current main** (verified by first-adding commit, so this lane no longer stacks on them):

| Item | Landing commit | Consequence for this lane |
| --- | --- | --- |
| #776 reference eligibility policy (`MusicReferenceEligibilityPolicy.cs` + tests) | `efd7c219 Integrate #776: feat(music): fail-closed audio/video reference eligibility policy (#708)` | Its evidence inputs are documented as "supplied by the asset/storage authorization path". That producer is this lane; the dependency now runs main → #805, not #776 → #805. |
| Fail-closed song request contract, validator and capability catalog | `7d0331ec integrate(music): fail-closed song request contract…` | The gate this lane is the precondition for exists on main and is closed. #709 remains open only for the dialect/lyrics extension on top of it. |

**Merge order that keeps every lane buildable:**

1. **#805 (this lane)** — first. It depends on nothing unmerged, compiles against current main, adds no route and changes no release input.
2. **#815** — re-cut onto #805's head (its `integration/music-reference-evidence-base-20261114` base still carries `c97e5890`, i.e. the producer *without* the retention/deletion refusals). #815 is where `Program.cs` gains the DI registration and `MusicReferenceEligibilityController.cs`/`MusicReferenceEligibilityBoundary.cs` first expose the join, so it is the first lane that can turn a pasted id into a server-verified read. Its security review must re-run after the refresh.
3. **#1116 and #1152** — test-only HTTP QA of that join; both also carry the stale copy and must be re-cut after #815.
4. **#774** (reference-video audio extraction), **#730** (reference upload validation), **#709** (dialect contract and any future capability flip) — independent of this lane's files; none may redefine the codes in §3.
5. **Controller wiring of references into generation** (the `MusicGenerationController` slice owned by #359's file) — last, and separately reviewed: it is the change that would finally let an authorized reference reach a provider request, so it must carry its own cost and rights evidence.

**Blocking observation for #1152, reproduced.** #1152 is currently RED at its head `a6c8e1d7` (API integration tests, run `44507341554`, job `136921432360`, `Failed: 2`), failing exactly the two axes this lane's head commit implements:

- `Reference_whose_deletion_the_server_already_recorded_is_refused_even_while_status_says_ready`
- `Retention_window_elapsed_reference_is_refused_at_the_access_layer`

Both failures are explained by the stale producer, not by #1152's assertions: in a throwaway local worktree at #1152's head, replacing only the lane's two producer files with `e98ce379` turned the class from `Failed: 2, Passed: 8` into `Failed: 0, Passed: 10`, and the surrounding reference family into `Failed: 0, Passed: 93`. No file outside this lane was modified and nothing was committed. The remediation belongs to #815/#1152's re-cut, and it is the concrete reason #805 must merge before them.

---

## 7. Verdict and outstanding conditions

**Evidence complete** for the candidate `e98ce379` against `main` `da8d8b55`: provenance (lineage, merge-ref integration, machine manifest reproduced off-CI), ownership (two new files, zero collisions, boundaries held, staleness of downstream copies quantified), API authorization (thirteen ordered fail-closed decisions on server-recorded state behind the shared visibility rule, with disclosure ordering and a leakage contract), provider/cost safety (no provider, no key, no job, no charge, no schema, no DI, no route), exact-head CI (7/7 green on current main, +38 test delta proving execution), and dependency order (main-side prerequisites landed; #815 → #1116/#1152 → controller wiring follow).

**Conditions outside this document's authority:**

1. `security-review-required` sign-off by an independent reviewer (`reviewDecision` is still empty).
2. Re-cut #815 onto this head, then #1116 and #1152, before any of them is considered mergeable; #1152 is RED until then.
3. A separate rights/provider-capability decision before any flip of `SupportsAuthorizedWorkspaceReferences`.
4. The authoritative release manifest for this code will be the post-merge `push` run of `Taslim Release Evidence`; §1's manifest is candidate evidence only, and the merge ref it names is garbage-collected when the PR is updated.

Nothing here merges, deploys, closes a PR, enables a provider, or touches Railway.
