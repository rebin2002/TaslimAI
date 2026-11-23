/**
 * Browser acceptance for the Music Studio structured-lyrics composer wiring
 * (step 1 of docs/MUSIC_STUDIO_PREFLIGHT_UI.md).
 *
 * The gap this closes: PR #772's `LyricsSectionEditor` and the provider-free
 * `MusicLyricsPreflightPanel` both landed as components with no consumer, so
 * nothing proved a member could reach either one from the studio they actually
 * use - and the rows typed into them disappeared on the first tab switch while
 * every sibling studio (Document, Presentation, Social) already keeps its unsent
 * brief browser-local. `MusicStudioView.tsx` now renders both halves inside one
 * labelled `<section className="music-lyrics-composition">` and persists what is
 * authored there through `musicStudioLyricsDraftState.ts`, which stores the
 * draft in sessionStorage under
 * `taslim:music-lyrics-draft:<url-encoded userId>:<url-encoded workspaceId>`.
 * These tests drive that surface in a real authenticated session and prove:
 *
 *   1. the landed editor and the mirrored contract panel are both on the compose
 *      surface inside one labelled section, and what is typed into a section row
 *      is re-measured by the panel as it is typed - the row appears with its
 *      contract token, line count and character count, the Sorani selection
 *      resolves to the `ku`/`sorani`/`rtl` vocabulary, and the handoff stays
 *      withheld (`data-ready="false"`, `data-blocked-reason`) until the lyrics
 *      rights attestation is ticked;
 *   2. a clean handoff is only ever recorded, never sent: with a canonical row,
 *      a contract language and the attestation, the relabelled
 *      "Check and keep this draft" control writes the notice that says nothing
 *      was submitted, the panel shows its own provider-capability copy, the
 *      studio's generate action stays inert, and the job route
 *      `POST /api/music-generation/jobs` is not requested even once;
 *   3. the authored draft and its attestation survive an unmount/remount (the
 *      away-and-back a member performs between studio tabs), and a draft stored
 *      under a different workspace scope is never replayed into this one.
 *
 * English locale only, deliberately. This copy is not localized yet: step 3 of
 * docs/MUSIC_STUDIO_PREFLIGHT_UI.md reserves moving `musicStudioLyricsLabels.ts`
 * into the `i18n.ts` catalogs, so the composer, editor and panel strings below
 * render in English in every locale, and asserting Arabic or Sorani versions of
 * them would assert text the product does not have. Only the surrounding studio
 * chrome is localized, and it is read from the English catalog.
 *
 * No provider is involved: the job-submission route is only counted, never
 * fulfilled, and nothing here waits on a generation job. The preflight panel is
 * provider-free by construction (`MusicLyricsPreflightPanel.tsx` performs no
 * fetch at all), which is what makes "the panel updated itself while the job
 * counter stayed at zero" a meaningful assertion rather than a coincidence.
 */
import { test, expect } from "./fixtures";

/**
 * Every literal below is copied from the file named beside it. e2e specs run
 * against the built app and may not import app modules, so these are literals
 * with their source recorded, and the character counts the panel mirrors are
 * derived from the same lyric strings the test types.
 */

// src/components/music/musicStudioLyricsLabels.ts: `musicStudioLyricsComposerCopy`.
const composerCopy = {
  eyebrow: "Structured lyrics",
  heading: "Write the song by section",
  subtitle:
    "Sections are checked against the same lyrics contract the server applies, and the draft stays on this device until sung lyrics are enabled for your provider.",
  lyricsRights: "I hold the rights to these lyrics.",
  referenceRights: "I have the rights to use the selected reference.",
  clearDraft: "Clear this lyrics draft",
  // `handoffRecorded(1)` - one authored row, so the singular branch of `section${…}`.
  handoffRecorded:
    "Draft recorded: 1 section shaped for a sung request. Nothing was submitted, because sung lyrics are not enabled for your provider yet.",
  // `handoffWithheld(1)` - the single `lyrics_rights_required` issue the blocked
  // draft carries, so again the singular branch of `issue${…}`.
  handoffWithheld:
    "Preflight withheld this draft: 1 contract issue to fix before it could ever be sent.",
} as const;

// src/components/music/musicStudioLyricsLabels.ts: `musicStudioLyricsEditorLabels`
// (`addSection`, `sectionType`, `lyricInput`, `emptySectionError`, `sectionsLimit`)
// composed with `lyricsSectionAriaLabel` from src/components/music/LyricsSectionEditor.tsx,
// which is `${sectionType(type, ordinal)} · ${ordinal}`.
const editorCopy = {
  addVerse: "Add verse",
  addChorus: "Add chorus",
  verseRow: "Verse 1",
  verseAriaLabel: "Verse 1 · 1",
  chorusRow: "Chorus 2",
  lyricPlaceholder: "Write the lyrics for this section, one line per lyric",
  emptySectionError: "Every section needs lyrics before this draft can be checked.",
  sectionsLimit: "Maximum 24 sections",
} as const;

// src/components/music/MusicLyricsPreflightPanel.tsx: `defaultMusicLyricsPreflightLabels`,
// except `readyNotice`, which the studio replaces from
// `musicStudioLyricsPreflightLabelOverrides` in musicStudioLyricsLabels.ts - the
// override is part of what test 2 pins.
const panelCopy = {
  heading: "Lyrics preflight",
  summaryHeading: "Request shape",
  languageLabel: "Vocal language",
  dialectLabel: "Dialect",
  directionLabel: "Direction",
  sectionsHeading: "Sections",
  emptySection: "(empty section)",
  issuesHeading: "Blocking issues",
  noIssues: "No mirrored contract violations in this draft.",
  capabilityHeading: "Provider capability",
  capabilityNotice:
    "Sung lyrics are not enabled for any configured provider, so this preflight checks the request shape only and starts no generation.",
  blockedNotice: "Preflight withheld: this draft would be rejected before cost admission.",
  readyNotice:
    "Preflight clean: the typed song payload is recorded in this draft. Sung lyrics stay gated on provider capability, so nothing is submitted.",
  handoffButton: "Check and keep this draft",
} as const;

// src/lib/i18n.ts, English catalog: music.title, music.moreOptions, music.language,
// music.language.ku, music.generate.
const studioCopy = {
  title: "Music Studio",
  moreOptions: "More options",
  language: "Language",
  soraniOption: "Kurdish Sorani",
  generate: "Generate music",
} as const;

// src/lib/musicStudioLyricsDraftState.ts: `draftStoragePrefix`, and the key
// `taslim:music-lyrics-draft:<encodeURIComponent(userId)>:<encodeURIComponent(workspaceId)>`.
const draftKeyPrefix = "taslim:music-lyrics-draft:";

// Sorani lyric lines. The panel counts characters exactly as the mirrored
// contract does (`splitLyricsDraftLines` then `line.length`, per
// src/lib/musicLyricsDraftPayload.ts), so the expected counts below are taken
// from these same strings rather than hard-coded.
const verseLyric = "لەو شەودا گۆرانییەکت نووسیم";
const chorusLyric = "بە ئازادی، بە خۆشەویستی";
// Another workspace's private line, seeded directly into sessionStorage to show
// a neighbouring scope is never read back into this one.
const foreignLyric = "ئەم دڵە هێشتا چاوەڕێی تۆیە";

test.describe("Music Studio structured-lyrics composer wiring", () => {
  test("renders the landed editor and the mirrored preflight panel as one labelled composer that re-measures every keystroke", async ({ authenticatedPage: page }) => {
    let jobSubmissions = 0;
    await page.route("**/api/music-generation/jobs", async (route) => {
      jobSubmissions += 1;
      await route.abort();
    });
    await page.addInitScript((locale) => {
      window.localStorage.setItem("taslim-locale", locale);
    }, "en");

    await page.goto("/create/music");
    await expect(page.getByRole("heading", { level: 1, name: studioCopy.title })).toBeVisible();

    // One labelled section holds the whole composer: the `<section
    // className="music-lyrics-composition">` in MusicStudioView.tsx is named by
    // its `music-lyrics-composition-title` heading, so the region below is that
    // section and nothing else.
    const composer = page.getByRole("region", { name: composerCopy.heading });
    await expect(composer).toHaveCount(1);
    await expect(composer.getByRole("heading", { level: 2, name: composerCopy.heading })).toBeVisible();
    await expect(composer.getByRole("heading", { level: 2, name: composerCopy.heading })).toHaveId("music-lyrics-composition-title");
    await expect(composer).toContainText(composerCopy.eyebrow);
    await expect(composer).toContainText(composerCopy.subtitle);
    // PR #772's editor is reachable: its add-row controls are the only source of
    // rows, and the studio renders no rows until one is added.
    await expect(composer.getByRole("button", { name: editorCopy.addVerse, exact: true })).toBeVisible();
    await expect(composer.getByRole("button", { name: editorCopy.addChorus, exact: true })).toBeVisible();
    // The provider-free panel is the composer's other half, inside the same region.
    const panel = composer.getByRole("region", { name: panelCopy.heading });
    await expect(panel).toHaveCount(1);
    await expect(panel.getByRole("heading", { level: 3, name: panelCopy.summaryHeading })).toBeVisible();
    await expect(panel.getByRole("heading", { level: 3, name: panelCopy.issuesHeading })).toBeVisible();
    // A draft with no rows at all is already measured: the contract's own
    // `sections_missing` code (musicLyricsDraftPayload.ts), echoed verbatim by
    // the panel's issue list.
    await expect(panel).toContainText(`${panelCopy.sectionsHeading}: 0`);
    await expect(panel.locator('[data-issue-code="sections_missing"]')).toHaveCount(1);

    // Author two rows through the editor's own add controls, then aim them at
    // Sorani. The vocal-language select lives inside
    // `<details className="music-optional-controls">` in MusicStudioView.tsx;
    // `<summary>` carries no Playwright-mapped implicit role and the header's own
    // locale picker shares the accessible name "Language" (i18n.ts
    // `navigation.language`), so the disclosure is opened by its label and the
    // select addressed inside its own markup rather than page-wide.
    await page.getByText(studioCopy.moreOptions, { exact: true }).click();
    const vocalLanguage = page.locator("details.music-optional-controls").getByLabel(studioCopy.language, { exact: true });
    await expect(vocalLanguage.getByRole("option", { name: studioCopy.soraniOption })).toBeAttached();
    await vocalLanguage.selectOption("ku");
    await expect(vocalLanguage).toHaveValue("ku");

    await composer.getByRole("button", { name: editorCopy.addVerse, exact: true }).click();
    await composer.getByRole("button", { name: editorCopy.addChorus, exact: true }).click();
    const verseRow = composer.getByRole("textbox", { name: editorCopy.verseRow });
    const chorusRow = composer.getByRole("textbox", { name: editorCopy.chorusRow });
    await expect(verseRow).toHaveCount(1);
    // `lyricsSectionAriaLabel` names the row for assistive tech; the accessible
    // name itself comes from `aria-labelledby`, so the composition is asserted on
    // the attribute it is written to.
    await expect(verseRow).toHaveAttribute("aria-label", editorCopy.verseAriaLabel);
    await expect(verseRow).toHaveAttribute("placeholder", editorCopy.lyricPlaceholder);
    // `lyricsInputDirection` / `lyricsInputLanguageTag` (LyricsSectionEditor.tsx)
    // turn the studio's `ku` selection into right-to-left editing and the
    // canonical `ckb` tag, so Sorani is never typed inside an English shell.
    await expect(verseRow).toHaveAttribute("dir", "rtl");
    await expect(verseRow).toHaveAttribute("lang", "ckb");
    await expect(composer).toContainText(editorCopy.sectionsLimit);
    // Two rows with nothing in them: every half of the wiring says so once per
    // row. The editor keeps its own `emptySectionError` alert on each textarea,
    // and the panel mirrors each row as `(empty section)` with one
    // `section_empty` issue behind it.
    const emptyMirrors = panel.getByRole("listitem").filter({ hasText: panelCopy.emptySection });
    const emptyErrors = composer.getByRole("alert").filter({ hasText: editorCopy.emptySectionError });
    await expect(emptyMirrors).toHaveCount(2);
    await expect(emptyErrors).toHaveCount(2);
    await expect(panel.locator('[data-issue-code="section_empty"]')).toHaveCount(2);
    await expect(verseRow).toHaveAttribute("aria-invalid", "true");
    await expect(chorusRow).toHaveAttribute("aria-invalid", "true");

    // Typing a Sorani line is re-measured against the mirrored contract, live.
    await verseRow.fill(verseLyric);
    const verseMirror = panel.getByRole("listitem").filter({ hasText: "verse -" });
    await expect(verseMirror).toHaveText(`verse - 1 lines, ${verseLyric.length} characters`);
    await expect(verseMirror).toHaveAttribute("data-empty", "false");
    await expect(verseMirror).toHaveAttribute("data-invisible-only", "false");
    await expect(verseRow).not.toHaveAttribute("aria-invalid", "true");
    await expect(panel).toContainText(`${panelCopy.sectionsHeading}: 2`);
    await expect(panel).toContainText(`${panelCopy.languageLabel}: ku`);
    await expect(panel).toContainText(`${panelCopy.dialectLabel}: sorani`);
    await expect(panel).toContainText(`${panelCopy.directionLabel}: rtl`);
    // The untouched row keeps gating the draft ...
    await expect(emptyMirrors).toHaveCount(1);
    await expect(emptyErrors).toHaveCount(1);

    // ... until it is written too.
    await chorusRow.fill(chorusLyric);
    const chorusMirror = panel.getByRole("listitem").filter({ hasText: "chorus -" });
    await expect(chorusMirror).toHaveText(`chorus - 1 lines, ${chorusLyric.length} characters`);
    await expect(chorusMirror).toHaveAttribute("data-invisible-only", "false");
    await expect(emptyMirrors).toHaveCount(0);
    await expect(composer.getByRole("alert")).toHaveCount(0);
    await expect(panel.locator('[data-issue-code="section_empty"]')).toHaveCount(0);
    // With both rows holding visible glyphs, the only thing left between this
    // draft and a handoff is the attestation the user has not made yet - and the
    // panel says so in its polite status region rather than by disabling it out
    // of the focus order.
    await expect(panel.locator('[data-issue-code="lyrics_rights_required"]')).toHaveCount(1);
    const status = panel.getByRole("status");
    await expect(status).toHaveCount(1);
    await expect(status).toHaveAttribute("aria-live", "polite");
    await expect(status).toHaveAttribute("data-ready", "false");
    await expect(status).toHaveText(panelCopy.blockedNotice);
    await expect(panel).toHaveAttribute("data-admissible", "false");
    await expect(panel).toHaveAttribute("data-blocked-reason", "lyrics_rights_required");
    // Composing measured nothing but local work: no job request left the browser.
    expect(jobSubmissions).toBe(0);
  });

  test("records a clean handoff into the draft and never submits it", async ({ authenticatedPage: page }) => {
    let jobSubmissions = 0;
    await page.route("**/api/music-generation/jobs", async (route) => {
      jobSubmissions += 1;
      await route.abort();
    });
    await page.addInitScript((locale) => {
      window.localStorage.setItem("taslim-locale", locale);
    }, "en");

    await page.goto("/create/music");
    const composer = page.getByRole("region", { name: composerCopy.heading });
    const panel = composer.getByRole("region", { name: panelCopy.heading });
    const status = panel.getByRole("status");

    await composer.getByRole("button", { name: editorCopy.addChorus, exact: true }).click();
    await page.getByText(studioCopy.moreOptions, { exact: true }).click();
    await page.locator("details.music-optional-controls").getByLabel(studioCopy.language, { exact: true }).selectOption("ku");
    // The chorus row is the first (and only) row here, so `sectionType` reads
    // "Chorus 1" and `lyricsSectionAriaLabel` composes "Chorus 1 · 1".
    await composer.getByRole("textbox", { name: "Chorus 1" }).fill(chorusLyric);
    await expect(status).toHaveAttribute("data-ready", "false");

    // The reference attestation is conditional markup: it only appears while a
    // reference clip is selected, and none is.
    await expect(page.getByLabel(composerCopy.referenceRights, { exact: true })).toHaveCount(0);
    const lyricsRights = page.getByLabel(composerCopy.lyricsRights, { exact: true });
    await expect(lyricsRights).not.toBeChecked();

    // The three things the mirrored contract needs for an admissible draft: a
    // canonical section with visible text, a contract language (`ku`, whose
    // dialect resolves to `sorani`), and `lyricsRightsConfirmed` - see
    // `buildMusicLyricsDraft` in src/lib/musicLyricsDraftPayload.ts and the
    // visible-glyph gate in MusicLyricsPreflightPanel.tsx.
    await lyricsRights.check();
    await expect(status).toHaveAttribute("data-ready", "true");
    await expect(status).toHaveText(panelCopy.readyNotice);
    await expect(panel).toContainText(panelCopy.noIssues);
    await expect(panel.getByRole("listitem").filter({ hasText: "chorus -" })).toHaveText(`chorus - 1 lines, ${chorusLyric.length} characters`);
    await expect(panel).toHaveAttribute("data-admissible", "true");
    await expect(panel).toHaveAttribute("data-blocked-reason", "none");

    // The panel's own capability claim is on screen next to that ready sentence,
    // so "ready" can never be read as "a song is coming".
    await expect(panel.getByRole("heading", { level: 3, name: panelCopy.capabilityHeading })).toBeVisible();
    await expect(panel).toContainText(panelCopy.capabilityNotice);

    // The studio relabelled this control: the panel's default would promise a
    // submit ("Preflight and submit"), which this lane cannot do.
    const handoff = panel.getByRole("button", { name: panelCopy.handoffButton });
    await expect(handoff).toHaveCount(1);
    await expect(handoff).toHaveAccessibleName(panelCopy.handoffButton);
    await expect(handoff).toBeEnabled();
    await handoff.click();

    const notice = page.locator("#music-lyrics-draft-notice");
    await expect(notice).toHaveCount(1);
    await expect(notice).toBeVisible();
    await expect(notice).toHaveRole("status");
    await expect(notice).toHaveAttribute("aria-live", "polite");
    await expect(notice).toHaveText(composerCopy.handoffRecorded);
    // The notice belongs to the composer, not to the panel: it is the studio's
    // own line about what the recorded handoff did and did not do.
    await expect(composer.locator("#music-lyrics-draft-notice")).toHaveCount(1);

    // Nothing was submitted, and nothing could have been: the studio's only
    // route to a job stays inert while creation is paused
    // (`MUSIC_STUDIO_PROVIDER_ACCESS` in src/lib/musicStudioPreflight.ts), and a
    // recording press is not a submit press.
    const generate = page.getByRole("button", { name: studioCopy.generate });
    await expect(generate).toBeDisabled();
    await expect(generate).toHaveAttribute("aria-disabled", "true");
    expect(jobSubmissions).toBe(0);
  });

  test("restores the authored draft and its attestation across a tab switch, scoped to its own workspace", async ({ authenticatedPage: page }) => {
    let jobSubmissions = 0;
    await page.route("**/api/music-generation/jobs", async (route) => {
      jobSubmissions += 1;
      await route.abort();
    });
    await page.addInitScript((locale) => {
      window.localStorage.setItem("taslim-locale", locale);
    }, "en");

    await page.goto("/create/music");
    const composer = page.getByRole("region", { name: composerCopy.heading });
    const panel = composer.getByRole("region", { name: panelCopy.heading });
    await composer.getByRole("button", { name: editorCopy.addVerse, exact: true }).click();
    const verseRow = composer.getByRole("textbox", { name: editorCopy.verseRow });
    await verseRow.fill(verseLyric);
    const lyricsRights = page.getByLabel(composerCopy.lyricsRights, { exact: true });
    await lyricsRights.check();

    // The studio mirrors the draft into sessionStorage on the state update that
    // follows typing, so that write is awaited rather than raced, exactly as
    // e2e/product-ad-draft-acceptance.spec.ts does for the same reason. Waiting
    // on the serialized `lyricsRightsConfirmed` field as well means the tab
    // switch below can never start from a half-written draft.
    await page.waitForFunction((prefix) => {
      for (let index = 0; index < window.sessionStorage.length; index += 1) {
        const key = window.sessionStorage.key(index);
        if (key?.startsWith(prefix) && window.sessionStorage.getItem(key)?.includes("lyricsRightsConfirmed")) return true;
      }
      return false;
    }, draftKeyPrefix);
    const draftKeys = await page.evaluate((prefix) => {
      const keys: string[] = [];
      for (let index = 0; index < window.sessionStorage.length; index += 1) {
        const key = window.sessionStorage.key(index);
        if (key?.startsWith(prefix)) keys.push(key);
      }
      return keys;
    }, draftKeyPrefix);
    expect(draftKeys).toHaveLength(1);
    const ownKey = draftKeys[0];
    // `<prefix><encodeURIComponent(userId)>:<encodeURIComponent(workspaceId)>`:
    // an owner and a workspace, and colons inside either id would be encoded, so
    // exactly four segments means the draft really is keyed to this session's
    // owner and this session's workspace.
    expect(ownKey.split(":")).toHaveLength(4);

    // Seed a second scope in the same tab: the same owner with a different
    // workspace id. fixtures.ts offers no cheap way to switch the authenticated
    // session's real workspace, and the genuine two-workspace case (plus the
    // fail-closed read: unreadable JSON, non-contract section tokens, rows past
    // the mirrored ceilings) is pinned by the unit suite in
    // src/lib/musicStudioLyricsDraftState.test.ts. What this proves in the
    // browser is that the reader honours the key it was given.
    await page.evaluate(({ key, text }) => {
      window.sessionStorage.setItem(`${key}-other-workspace`, JSON.stringify({
        sections: [{ id: "other-workspace-chorus", type: "chorus", text }],
        lyricsRightsConfirmed: false,
        referenceRightsConfirmed: false,
      }));
    }, { key: ownKey, text: foreignLyric });

    // The tab switch: another studio unmounts the composer, coming back remounts
    // it. sessionStorage is per tab, so this is exactly the away-and-return a
    // member performs.
    await page.goto("/assets");
    await page.goto("/create/music");

    await expect(verseRow).toHaveCount(1);
    await expect(verseRow).toHaveValue(verseLyric);
    await expect(lyricsRights).toBeChecked();
    // The clear affordance only exists while rows are held, so its return is the
    // restored draft being real rather than a placeholder left on screen.
    await expect(composer.getByRole("button", { name: composerCopy.clearDraft, exact: true })).toBeVisible();
    await expect(panel.getByRole("listitem").filter({ hasText: "verse -" })).toHaveText(`verse - 1 lines, ${verseLyric.length} characters`);
    // Exactly one row came back - this workspace's - and the neighbouring scope
    // was neither merged nor substituted: had the key been ignored, the restored
    // row would read "Chorus 1" with the foreign line and the attestation would
    // have come back unchecked.
    await expect(panel).toContainText(`${panelCopy.sectionsHeading}: 1`);
    await expect(composer.getByRole("heading", { level: 4, name: "Chorus 1" })).toHaveCount(0);
    await expect(page.locator("main")).not.toContainText(foreignLyric);
    // A recorded handoff is not part of the draft, so the remounted composer is
    // quiet rather than repeating yesterday's notice.
    await expect(page.locator("#music-lyrics-draft-notice")).toHaveCount(0);
    // Restoring private lyrics to a browser tab involves no request at all.
    expect(jobSubmissions).toBe(0);
  });
});
