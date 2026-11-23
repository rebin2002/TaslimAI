# Music Studio lyrics preflight UI slice (issue #708)

Owner: the Music Studio preflight integration lane. This document describes
`apps/web/src/components/music/MusicLyricsPreflightPanel.tsx`, the first
provider-free end-to-end preflight surface for Sorani and Arabic lyrics, and
the exact contract it mirrors.

It now also describes the consumer of that surface: the structured-lyrics
composer wired into `apps/web/src/components/MusicStudioView.tsx`, which puts
PR #772's `LyricsSectionEditor` and this panel on the compose screen and keeps
what is typed there in a browser-local saved draft. That is follow-up step 1,
and it is recorded below rather than removed, so the contract-mirror guard that
pinned it can be satisfied by the wiring itself.

Related, already-merged work: PR #802 (client mapper), PR #826 (the fail-closed
song request contract from PR #709 plus the Sorani roundtrip test from PR #796).
Related blockers are inventoried in `docs/MUSIC_STUDIO_ARCHITECTURE.md`; this
file deliberately does not duplicate that table.

## What the slice is

One new presentational component plus a pure evaluator that takes an authored
structured-lyrics draft and answers, before any submit exists:

1. which contract language and dialect the selection resolves to,
2. which mirrored rule or limit would reject the request,
3. whether the text has any glyph a reader can see at all, and
4. what the single typed song payload would be if all three answers are clean.

```
authored sections + vocal language/dialect + rights attestations
        |
        +--> buildMusicLyricsDraft(input, "preflight")   (PR #802 helper, unmodified)
        |        -> issues[] and typed payload
        +--> visible-glyph gate                          (this slice)
        |        -> lyrics_invisible_only per row
        v
MusicLyricsPreflightResult { admissible, blockedReason, payload, ... }
        v
decideMusicLyricsPreflightHandoff -> { result, handoff }
```

`handoff` is the only gate a future submit path should trust. It is true only
when the mirrored contract is clean, the payload is shaped, the song-mode
vocabulary is pinned, and no row is invisible-only.

## Contract direction

The server is the source of truth and the browser mirrors it, exactly as
`docs/MUSIC_STUDIO_ARCHITECTURE.md` requires. `apps/api.Tests/MusicLyricsDraftContractMirrorTests.cs`
enforces that direction mechanically: vocabulary (`intro`...`refrain`), the
three lyric languages (`en`, `ar`, `ku`), all eight dialect tokens and the
single language each belongs to, every numeric limit (24 sections, 30 lines per
section, 200 characters per line, 3,500 characters total, 10 reference assets),
the pinned `song_with_vocals` / `vocal` mode fields, and the default-dialect
rule. Any drift in either direction fails CI rather than quietly producing a
draft the server would reject.

Dialect behaviour that the panel preserves rather than improves:

| Selection | Resolved dialect | Why |
| --- | --- | --- |
| `ku` / `ckb` | `sorani` | Sorani is the Kurdish dialect the product names (#708). |
| `ku` + `sorani_erbil` or `sorani_sulaymaniyah` | as chosen | Regional tokens carry user intent only. |
| `ar` alone | unresolved, `dialect_required` | Arabic has no silent default; the dialect is a user decision. |
| `ar` + `iraqi_baghdadi`, `iraqi_mosul`, `iraqi_basra`, `gulf_arabic`, `levantine_arabic` | as chosen | Modelled vocabulary, not advertised capability. |
| `ku` + an Arabic token, or `ar` + `sorani` | unresolved, `dialect_mismatch` | A token belongs to exactly one language. |
| `en` + any token | unresolved, `dialect_not_allowed` | English never takes a dialect. |
| `ku` + `hkodi` | unresolved, `dialect_unknown` | Unknown tokens never fall back. |

Direction and script metadata match the studio's own helpers: Sorani and
Arabic render `dir="rtl"` with `lang="ckb"` and `lang="ar"`, English renders
`dir="ltr"` with `lang="en"`.

## Invisible-glyph gate, and what it does not replace

`musicLyricsVisibleGlyphCount` counts a character as visible unless it is a Cc
control, a Cf format character (including U+061C ARABIC LETTER MARK and U+200F
RIGHT-TO-LEFT MARK), a space of any width (including U+200B, U+200C and U+FEFF),
or a Zl/Zp line separator. A row whose every character is invisible is reported
as `lyrics_invisible_only` and the payload is withheld.

This is a **client-side** hardening of the draft UX. It does not close the
server-side bug that PR #773 pins in red: `MusicGenerationRequestValidator`
still measures `Description` and `Purpose` with `Trim().Length >= 3`, and
bidi/zero-width characters satisfy that gate. The fix belongs to PR #813, which
is stacked on PR #709's contract branch, and neither file is this lane's to
edit. This slice therefore adds no server gate, does not touch the validator,
and does not merge or supersede #773 or #813. Anyone landing #709 should land
#813 with it and then #773 as the regression lock, in that order.

## Accessibility and keyboard semantics

The panel is a preview, but it owns one decision control, so its keyboard
semantics are part of the contract rather than decoration.

| Surface | Semantics | Why |
| --- | --- | --- |
| Handoff control | `aria-disabled="true"` while blocked, and the attribute is absent when clean | A natively `disabled` button leaves the sequential focus order, so a keyboard-only user could not reach the preview's primary action at all and could never learn which rule withheld the draft. |
| Activation while blocked | calls `onBlocked` with the full result, and never `onHandoff` | Fail-closed stays fail-closed. While the control was natively disabled React dispatched no click, so the documented `onBlocked` callback was unreachable code and keyboard activation did nothing, silently. |
| Ready/blocked sentence | `role="status"`, `aria-live="polite"`, addressed by id | It is the only prose that explains the gate. The control carries `aria-describedby` for it, so the reason is heard on focus, before activation, and again when the draft flips. |
| Blocking-issue list | the live region holds only the list, never its `Blocking issues` heading | A heading inside a live region is re-read on every keystroke, which buries the violation that actually changed. |
| Region ids | `useId()` per instance | `aria-labelledby` and `aria-describedby` resolve document-wide. One shared literal id named every panel after the first heading and pointed every control at the first status sentence, so a second preview was labelled with the wrong draft's state. |

None of this echoes authored lyric text into an announced region: the live
regions carry issue codes, counts and state prose only, which keeps the
no-content-leak property the rendering tests already pin.

`apps/web/src/components/music/MusicLyricsPreflightPanel.accessibility.test.tsx`
pins the five rows above against a mounted panel, using the dependency-free
test-only DOM shim style already used by `ProjectContextEditor.test.tsx`, so
keyboard activation and live-region structure are asserted rather than inferred
from a markup string. No test dependency and no package manifest changed.

## Capability posture

Nothing here enables singing. `MusicGenerationCapabilityCatalog.ForProvider`
returns `SupportsLyricsToVocals: false` with empty language and dialect sets for
every provider key on current `main`, and the panel's own copy states that sung
lyrics are not enabled for any configured provider. `MusicVocalCapabilityFailClosedTests`
keeps that wording honest: if a future adapter gains the capability, the panel
assertion fails and the copy must be revisited with evidence, not silently.

A clean preflight means "this draft matches the request contract". It has never
meant "a provider will sing it", and the slice contains no provider call, no
upload, no cost preview request, no charging path, and no provider activation.
The panel performs no network or storage work at all; the test asserts that
structurally by scanning its own source.

## Ownership boundaries respected

Audited file-by-file against every open PR before each branch was cut (483 at
the original slice, 523 at the accessibility follow-up, 404 at the studio
wiring). These paths are not claimed by any open PR:

- `apps/web/src/components/music/MusicLyricsPreflightPanel.tsx` (new)
- `apps/web/src/components/music/MusicLyricsPreflightPanel.test.tsx` (new)
- `apps/web/src/components/music/MusicLyricsPreflightPanel.accessibility.test.tsx`
  (new, accessibility follow-up)
- `apps/api.Tests/MusicLyricsDraftContractMirrorTests.cs` (new)
- `docs/MUSIC_STUDIO_PREFLIGHT_UI.md` (this file)
- `apps/web/src/lib/musicStudioLyricsDraftState.ts` (+ its test file) (new,
  studio wiring)
- `apps/web/src/components/music/musicStudioLyricsLabels.ts` (new, studio
  wiring)
- `apps/web/e2e/music-lyrics-section-wiring.acceptance.spec.ts` (new, studio
  wiring)

The accessibility follow-up changes only the panel and its own new test file.
`dir` and `lang` semantics stay exactly as the merged direction fix (#758)
published them, and the mirrored vocabulary in `musicLyricsDraftPayload.ts` is
untouched: no rule, limit or dialect behaviour changed, so the contract mirror
and the server-side lanes are unaffected.

Deliberately untouched, with their owners:

| Path | Owner | Why untouched |
| --- | --- | --- |
| `LyricsSectionEditor.tsx` (+ test) | PR #772 | The editor is that lane's deliverable; this panel consumes its row model (`id`, `type`, `text`) rather than duplicating it. |
| `MusicStudioView.test.tsx` | the #560/#561 lineage | The studio's own submit-path tests stay theirs; the wiring adds a separate test file rather than editing that one. |
| `MusicGenerationContracts.cs`, `MusicGenerationRequestValidator.cs`, `MusicGenerationCapabilityCatalog.cs`, `MusicGenerationSongContractTests.cs`, `MusicInvisibleTextAdmissionTests.cs` | draft PR #709 (with #260, #812 as other claimants) | Read-only mirror assertions only. |
| `MusicInvisibleLyricsCostAdmissionTests.cs` | draft PR #773 | Its red contract stays red until #813 lands. |
| `musicLyricsDraftPayload.ts` | merged PR #802 | Mirrored vocabulary must change server-side first. |
| `musicStudioState.ts` | open PRs #572/#573 | Not needed by this slice. |
| `i18n.ts`, `api.ts`, `Program.cs` | multiple open lanes | No locale keys or client transport added; labels are inline with a prop override. |
| Voice and Direct Video contracts | draft PR #837 | Different lane; no overlap. |

`MusicLyricsDraftContractMirrorTests.StructuredLyricsEditorStaysSingleSourcedAndTheWiringFollowUpStaysRecorded`
keeps the one dependency that matters in force after PR #772 landed its editor:
exactly one structured lyrics editor may exist, it must mirror the contract
vocabulary in `musicLyricsDraftPayload.ts` rather than restate it, and until
`MusicStudioView.tsx` consumes it the step-1 sentence below must stay in this
document - so this panel cannot sit unused and a second editor cannot appear.
The guard is satisfied by its `wired` branch now: `MusicStudioView.tsx` imports
and renders `LyricsSectionEditor`, so the pinned step-1 sentence no longer has
to be present for the assertion to hold, and re-adding it would record work
that is already finished.

## Studio wiring (follow-up step 1)

`MusicStudioView.tsx` renders one labelled `<section className="music-lyrics-composition">`
after the reference-audio panel, holding three things and nothing else: PR #772's
`LyricsSectionEditor`, the rights attestations the contract asks for, and this
lane's `MusicLyricsPreflightPanel`. Authored rows are measured on every keystroke
by the same mirrored contract the server applies, and the panel's handoff control
is relabelled to "Check and keep this draft" because there is nothing to hand off
*to*: `MusicGenerationCapabilityCatalog` still reports
`SupportsLyricsToVocals: false` for every provider, the client
`MusicGenerationInput` has no lyrics fields at all, and the submit path is
untouched. A clean handoff records the section count into a browser-local notice.

The draft itself follows the pattern every sibling studio already uses
(`documentStudioDraftState.ts`): a `sessionStorage` entry keyed
`taslim:music-lyrics-draft:<userId>:<workspaceId>`, owner-scoped, restored once
per owner scope, never mirrored while a generation job owns the surface, dropped
when it becomes empty, and parsed fail-closed - rows whose section token is not
in the contract vocabulary, blank rows, rows past the 24-section ceiling and text
past the 3,500-character ceiling are discarded or truncated at line boundaries
rather than trusted. Nothing private leaves the browser and no locale key or
transport change is involved.

Ownership hazard, stated plainly: the wiring was audited against 404 open PRs,
and `MusicStudioView.tsx` was the most contended file in the area with 18
claimants (`api.ts` 68, `i18n.ts` 63, `musicStudioState.ts` 17). Of the two lanes
the original note named, PR #560 closed unmerged and PR #561 is still open on
`ci: validate Music Studio workspace response freshness`, so this diff is the
smaller one - four import blocks, one contiguous state block, one new JSX
section, and one `id="music-vocal-language"` attribute added to the studio's
existing language select. That attribute is the only pre-existing line this lane
edits, and it exists because a `<select>` wrapped in its own `<label>` folds its
option text into its computed accessible name, so a browser test cannot address
the field by label - the same reason `#document-output-format` is addressed by
id elsewhere in the suite. The branch must be rebased, not re-derived, if #561
lands first.

## Follow-ups, in order

1. ~~Wire the landed #772 editor to this panel in `MusicStudioView.tsx`.~~
   **Landed.** The editor, the attestations and the panel now share one composer
   section, and the authored draft survives a tab switch. The rebase obligation
   that came with it is open while PR #561 is: whoever merges next rebases the
   composer block, not the contract.
2. Land #709 with #813, then #773, which makes the server gate match the client
   gate this slice already applies.
3. Locale keys for the panel copy, once `i18n.ts` is free of its open lanes.
   The composer strings live in `musicStudioLyricsLabels.ts` for exactly that
   reason: one file to replace when the catalogs open up, instead of a hunt
   through the most contended view in the area.
4. Any capability change (reference conditioning, dialect enablement) requires
   the blockers in `docs/MUSIC_STUDIO_ARCHITECTURE.md` to clear first; the
   fail-closed tests here will fail until that evidence exists.

## Checks this slice is expected to pass

All seven hosted checks on a PR based at `main`: Deployment smoke helper
contracts, Frontend checks, API integration tests, Browser E2E, Autopilot
next-wave gate, Production container smoke, and Release evidence and safety
contract. Targeted local equivalents: the new panel test file, the full web
unit suite, `tsc --noEmit`, `eslint`, and the two new C# test classes.
For the wiring, the targeted equivalents are
`src/lib/musicStudioLyricsDraftState.test.ts`,
`src/components/MusicStudioView.lyricsWiring.test.tsx`, the new Playwright
acceptance spec, and
`MusicLyricsDraftContractMirrorTests.StructuredLyricsEditorStaysSingleSourcedAndTheWiringFollowUpStaysRecorded`
- which must keep passing with the pinned step-1 sentence gone, because the
wiring is what carries it now.

## Composer styling (the presentation pass on step 1)

Step 1 put the composer on the studio, and PR #772's editor and panel shipped with
20 class names that had no rule anywhere in `apps/web/src/app/globals.css`: the
studio scopes its field styling to `.music-primary-field` / `.music-choice-field`,
so nothing was inherited either. The lyric textareas fell back to browser defaults
on a dark page, the eight add-section controls rendered as unspaced bare text, and
every state the mirrored contract publishes through `data-*` attributes
(`data-empty`, `data-invisible-only`, `data-ready`, `data-issue-code`,
`aria-invalid`, `aria-disabled`) was invisible. The block appended at the end of
`globals.css` closes that, using only the `--music-*` tokens declared on
`.music-studio-page` and the same card, field, notice and error language the rest
of the studio already uses.

It is presentational only, which is why it is a safe next step and reviewable on
its own:

- it adds no submit path and touches no request. `MUSIC_STUDIO_PROVIDER_ACCESS`
  stays `paused`, no cost-admission surface changes, and the paused provider
  notice and the generate button are deliberately out of scope: their colours are
  the studio's claim about capability, not a styling question.
- it states contract facts and never authors them. Every visual state is keyed to
  an attribute the mirrored contract or `LyricsSectionEditor` already renders, and
  no `content` declaration generates prose, so a stylesheet cannot claim
  readiness, a dialect, or a provider capability.
- it preserves #772's accessibility decision. The blocked handoff control is
  `aria-disabled` rather than `disabled` so that it stays in the sequential focus
  order, so the block contains no `display: none`, `visibility: hidden` or
  `pointer-events` rule anywhere, and it gives the composer the studio's `#8de0d1`
  focus-visible ring, which before this pass was scoped to `.music-reference-panel`
  and so left the composer with no keyboard focus indicator at all.
- it is right-to-left safe by construction: logical properties only, so Sorani
  (`ckb`) and Arabic indent and inset on the correct edge, asserted from computed
  styles in the acceptance spec rather than assumed.

`src/app/musicLyricsStudioStyles.test.ts` pins the block and derives the class list
from `LyricsSectionEditor.tsx`, `MusicLyricsPreflightPanel.tsx` and the composer in
`MusicStudioView.tsx` instead of restating it, so a class added to a component
fails the suite until it is styled. The fourth test of
`e2e/music-lyrics-section-wiring.acceptance.spec.ts` reads what a browser does with
the same rules: card and field geometry, the invalid-row colour, the keyboard focus
ring inside the composer, a blocked handoff that stays visible and hit-testable,
the RTL mirror measured physically, and 40px touch targets at 390px.

Still open, and deliberately not attempted here: an `ar` (Arabic) draft can never
reach `data-ready="true"`, because the mirrored contract requires a dialect for
`ar` and there is no dialect control. Follow-up 4 gates dialect enablement on the
capability evidence in `docs/MUSIC_STUDIO_ARCHITECTURE.md`, and every provider's
`SupportedLyricsDialects` is pinned empty by `MusicVocalCapabilityFailClosedTests`,
so a picker today would advertise a capability no provider has. This pass makes
that dead end legible - the `dialect_required` issue now reads as an issue -
without pretending to fix it.
