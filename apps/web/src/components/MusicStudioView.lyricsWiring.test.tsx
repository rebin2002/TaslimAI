/**
 * Wiring tests for the structured lyrics surface inside Music Studio.
 *
 * Follow-up step 1 of `docs/MUSIC_STUDIO_PREFLIGHT_UI.md` existed because two
 * landed components - PR #772's `LyricsSectionEditor` and the provider-free
 * `MusicLyricsPreflightPanel` - had no consumer. These cases pin the three
 * things that make the wiring real rather than cosmetic:
 *
 *   1. both components render inside the studio's compose form, as one
 *      labelled section, with the studio's own vocal language and the mirrored
 *      limits, and the studio's single submit route is untouched;
 *   2. the authored rows reach the mirrored contract through the studio's own
 *      adapter, so the panel's clean/blocked reading is the same reading the
 *      studio acts on - including the cases where it must refuse;
 *   3. nothing in this lane can send lyrics, because sung lyrics are still
 *      gated on provider capability, which is false for every configured
 *      provider. A wiring that quietly attached a payload to the generation
 *      request would be a defect, so it is asserted against.
 */
import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import {
  MUSIC_LYRICS_MAX_LINE_CHARACTERS,
  MUSIC_LYRICS_MAX_LINES_PER_SECTION,
  MUSIC_LYRICS_MAX_TOTAL_CHARACTERS,
} from "@/lib/musicLyricsDraftPayload";
import {
  LYRICS_MAX_CHARS_PER_SECTION,
  LyricsSectionEditor,
} from "@/components/music/LyricsSectionEditor";
import { MusicStudioView } from "./MusicStudioView";
import {
  MusicLyricsPreflightPanel,
  decideMusicLyricsPreflightHandoff,
} from "@/components/music/MusicLyricsPreflightPanel";
import {
  musicStudioLyricsComposerCopy,
  musicStudioLyricsEditorLabels,
  musicStudioLyricsEditorLimits,
  musicStudioLyricsPreflightLabelOverrides,
} from "@/components/music/musicStudioLyricsLabels";
import {
  buildMusicStudioLyricsPreflightInput,
  type MusicStudioLyricsDraftState,
  type MusicStudioLyricsSelection,
} from "@/lib/musicStudioLyricsDraftState";

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ user: { id: "user-1" }, workspace: { id: "workspace-1" } }),
}));
vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: (key: string) => key }),
}));
vi.mock("@/lib/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api")>();
  return {
    ...actual,
    api: {
      ...actual.api,
      listProjects: vi.fn(),
      listAssets: vi.fn(),
      getGenerationJob: vi.fn(),
      createMusicGenerationJob: vi.fn(),
      cancelGenerationJob: vi.fn(),
    },
  };
});

const studioSource = readFileSync(new URL("./MusicStudioView.tsx", import.meta.url), "utf8");

const soraniDraft: MusicStudioLyricsDraftState = {
  sections: [
    { id: "s1", type: "verse", text: "Bist dengê çiyayan di şevê de\nRêya kurdî dirêj e" },
    { id: "s2", type: "chorus", text: "ئەڵێم بگەڕێمەوە بۆ ئاگری دڵمە" },
  ],
  lyricsRightsConfirmed: true,
  referenceRightsConfirmed: false,
};

const cleanSelection: MusicStudioLyricsSelection = { vocalLanguage: "ku" };

/** What the studio's own adapter says about a draft, in the panel's terms. */
function handoffFor(
  state: MusicStudioLyricsDraftState,
  selection: MusicStudioLyricsSelection = cleanSelection,
) {
  return decideMusicLyricsPreflightHandoff(buildMusicStudioLyricsPreflightInput(state, selection));
}

describe("the wired studio surface", () => {
  function studioMarkup(): string {
    // Server rendering is what a visitor to /create/music sees before any
    // workspace request settles, which is exactly when the lyrics composer
    // must already be on screen rather than arriving after a fetch.
    return renderToStaticMarkup(<MusicStudioView />);
  }

  it("renders the landed section editor and the preflight panel as one labelled studio section", () => {
    const markup = studioMarkup();
    expect(markup).toContain('class="music-lyrics-composition"');
    expect(markup).toContain('aria-labelledby="music-lyrics-composition-title"');
    expect(markup).toContain(`id="music-lyrics-composition-title">${musicStudioLyricsComposerCopy.heading}</h2>`);
    expect(markup).toContain(musicStudioLyricsComposerCopy.eyebrow);
    expect(markup).toContain(musicStudioLyricsComposerCopy.subtitle);
    // Both landed components, in this order, inside the same section.
    expect(markup.indexOf("lyrics-section-editor")).toBeGreaterThan(-1);
    expect(markup.indexOf("music-lyrics-preflight")).toBeGreaterThan(markup.indexOf("lyrics-section-editor"));
    expect(markup).toContain(musicStudioLyricsPreflightLabelOverrides.heading);
  });

  it("offers the contract vocabulary through the studio's own labels", () => {
    const markup = studioMarkup();
    // One add control per mirrored section type, named the way the studio
    // names it - the editor contributes no copy of its own.
    for (const type of ["intro", "verse", "pre_chorus", "chorus", "post_chorus", "bridge", "outro", "refrain"]) {
      expect(markup).toContain(musicStudioLyricsEditorLabels.addSection(type as never));
    }
    // The composer starts with no rows: nothing is auto-generated for the
    // user, and no invented first verse appears in the studio.
    expect(markup).not.toContain("lyrics-section-editor-item");
    expect(markup).toContain(musicStudioLyricsEditorLimits.maxSections.toString());
  });

  it("renders authored rows through the studio's labels, in the lyric script's direction", () => {
    // Rendered the way the studio renders it, so the accessible names, the
    // placeholder and the bidi direction are the studio's, not the test's.
    const markup = renderToStaticMarkup(
      <LyricsSectionEditor
        value={soraniDraft.sections}
        language="ku"
        labels={musicStudioLyricsEditorLabels}
        maxSections={musicStudioLyricsEditorLimits.maxSections}
        onChange={() => undefined}
      />,
    );
    expect(markup).toContain(`aria-label="${musicStudioLyricsEditorLabels.sectionType("verse", 1)} · 1"`);
    expect(markup).toContain(musicStudioLyricsEditorLabels.lyricInput);
    expect(markup).toContain('dir="rtl"');
    expect(markup).toContain('lang="ckb"');
    expect(markup).toContain("ئەڵێم بگەڕێمەوە");
    expect(markup).toContain(musicStudioLyricsEditorLabels.removeSection(2));
  });

  it("states the mirrored limits instead of inventing its own", () => {
    const markup = studioMarkup();
    // The studio passes only the section ceiling, so the per-section limit
    // shown is the editor's mirrored default rather than a second number.
    expect(markup).toContain(musicStudioLyricsEditorLabels.charactersLimit(LYRICS_MAX_CHARS_PER_SECTION));
    expect(markup).toContain(musicStudioLyricsEditorLabels.totalCharactersLimit(MUSIC_LYRICS_MAX_TOTAL_CHARACTERS));
    expect(markup).toContain(musicStudioLyricsEditorLabels.sectionsLimit(musicStudioLyricsEditorLimits.maxSections));
  });

  it("keeps the studio's single submit route and gives the handoff no route at all", () => {
    const markup = studioMarkup();
    // The handoff control is a button, never a second submit; the studio keeps
    // exactly one way to leave the browser.
    const submitButtons = markup.match(/type="submit"/g) ?? [];
    expect(submitButtons).toHaveLength(1);
    expect(markup).toContain(musicStudioLyricsPreflightLabelOverrides.submitLabel);
    expect(markup).not.toContain("Preflight and submit");
    // The capability copy is the panel's own claim, shown rather than improved.
    expect(markup).toContain("Sung lyrics are not enabled for any configured provider");
  });

  it("asks for the lyrics-rights attestation, and only asks for the reference one when a reference exists", () => {
    const markup = studioMarkup();
    expect(markup).toContain(musicStudioLyricsComposerCopy.lyricsRightsLabel);
    expect(markup).not.toContain(musicStudioLyricsComposerCopy.referenceRightsLabel);
    expect(musicStudioLyricsComposerCopy.referenceRightsLabel).toContain("selected reference");
  });
});

describe("the studio adapter against the mirrored contract", () => {
  it("hands off a clean Sorani draft with the vocabulary the server names", () => {
    const { result, handoff } = handoffFor(soraniDraft);
    expect(handoff).toBe(true);
    expect(result.payload).not.toBeNull();
    expect(result.payload?.lyrics.map((section) => section.section)).toEqual(["verse", "chorus"]);
    expect(result.payload?.lyrics[0]?.lines).toHaveLength(2);
    expect(result.payload?.lyricsLanguage).toBe("ku");
    expect(result.payload?.lyricsDialect).toBe("sorani");
    expect(result.payload?.creationMode).toBe("song_with_vocals");
    expect(result.payload?.vocalPreference).toBe("vocal");
  });

  it("refuses without the rights attestation, which the studio holds in the draft", () => {
    const { result, handoff } = handoffFor({ ...soraniDraft, lyricsRightsConfirmed: false });
    expect(handoff).toBe(false);
    expect(result.issues.map((issue) => issue.code)).toContain("lyrics_rights_required");
    expect(result.payload).toBeNull();
  });

  it("refuses the studio's own default language rather than guessing one", () => {
    // `auto` is a legitimate studio selection and is not a contract lyric
    // language; the panel must be the one to say so.
    const { result, handoff } = handoffFor(soraniDraft, { vocalLanguage: "auto" });
    expect(handoff).toBe(false);
    expect(result.issues.map((issue) => issue.code)).toContain("language_unsupported");
  });

  it("refuses Arabic with no dialect, and English with one", () => {
    const arabic = handoffFor(soraniDraft, { vocalLanguage: "ar" });
    expect(arabic.handoff).toBe(false);
    expect(arabic.result.issues.map((issue) => issue.code)).toContain("dialect_required");

    const englishWithDialect = handoffFor(
      { ...soraniDraft, sections: [{ id: "s1", type: "verse", text: "Count the cost in plain words" }] },
      { vocalLanguage: "en", dialect: "sorani" },
    );
    expect(englishWithDialect.handoff).toBe(false);
    expect(englishWithDialect.result.issues.map((issue) => issue.code)).toContain("dialect_not_allowed");
  });

  it("refuses rows outside the mirrored limits", () => {
    const tooManyLines = handoffFor({
      ...soraniDraft,
      sections: [{
        id: "s1",
        type: "verse",
        text: Array.from({ length: MUSIC_LYRICS_MAX_LINES_PER_SECTION + 1 }, (_, index) => `line ${index}`).join("\n"),
      }],
    });
    expect(tooManyLines.result.issues.map((issue) => issue.code)).toContain("lines_too_many");

    const lineTooLong = handoffFor({
      ...soraniDraft,
      sections: [{ id: "s1", type: "verse", text: "k".repeat(MUSIC_LYRICS_MAX_LINE_CHARACTERS + 1) }],
    });
    expect(lineTooLong.result.issues.map((issue) => issue.code)).toContain("line_too_long");

    const totalTooLong = handoffFor({
      ...soraniDraft,
      sections: Array.from({ length: 10 }, (_, index) => ({
        id: `s${index}`,
        type: "verse",
        text: "m".repeat(MUSIC_LYRICS_MAX_TOTAL_CHARACTERS),
      })),
    });
    expect(totalTooLong.result.issues.map((issue) => issue.code)).toContain("lyrics_total_too_long");
  });

  it("refuses a row of nothing but invisible glyphs", () => {
    const invisibleOnly = handoffFor({
      ...soraniDraft,
      sections: [{ id: "s1", type: "verse", text: "‏ ‍  ​" }],
    });
    expect(invisibleOnly.handoff).toBe(false);
    expect(invisibleOnly.result.sections[0]?.visibleGlyphCount).toBe(0);
    expect(invisibleOnly.result.invisibleOnlyLyrics).toBe(true);
  });

  it("refuses a local reference file that has no workspace asset behind it yet", () => {
    const localReference = handoffFor(soraniDraft, {
      vocalLanguage: "ku",
      reference: { mediaType: "audio", assetId: null },
    });
    expect(localReference.handoff).toBe(false);
    expect(localReference.result.issues.map((issue) => issue.code)).toContain("reference_asset_invalid");
  });

  it("hands off a stored reference only when its own attestation is on the draft", () => {
    const assetId = "8f1c0d2e-3b4a-5c6d-7e8f-9a0b1c2d3e4f";
    const unattested = handoffFor(soraniDraft, { vocalLanguage: "ku", reference: { mediaType: "audio", assetId } });
    expect(unattested.handoff).toBe(false);
    expect(unattested.result.issues.map((issue) => issue.code)).toContain("reference_rights_required");

    const attested = handoffFor(
      { ...soraniDraft, referenceRightsConfirmed: true },
      { vocalLanguage: "ku", reference: { mediaType: "audio", assetId } },
    );
    expect(attested.handoff).toBe(true);
    expect(attested.result.payload?.referenceAssets).toEqual([{ mediaType: "audio", assetId }]);
    expect(attested.result.payload?.referenceRightsConfirmed).toBe(true);
  });

  it("renders the studio's own notices for both readings", () => {
    const clean = renderToStaticMarkup(
      <MusicLyricsPreflightPanel
        input={buildMusicStudioLyricsPreflightInput(soraniDraft, cleanSelection)}
        labels={musicStudioLyricsPreflightLabelOverrides}
      />,
    );
    expect(clean).toContain('data-ready="true"');
    expect(clean).toContain(musicStudioLyricsPreflightLabelOverrides.readyNotice);
    expect(clean).toContain(musicStudioLyricsPreflightLabelOverrides.submitLabel);

    const blocked = renderToStaticMarkup(
      <MusicLyricsPreflightPanel
        input={buildMusicStudioLyricsPreflightInput(soraniDraft, { vocalLanguage: "auto" })}
        labels={musicStudioLyricsPreflightLabelOverrides}
      />,
    );
    expect(blocked).toContain('data-ready="false"');
    expect(blocked).toContain("Preflight withheld");
  });
});

describe("the wiring stays inside its ownership boundary", () => {
  it("consumes both landed components from MusicStudioView itself", () => {
    // The contract-mirror guard in apps/api.Tests reads this file to decide
    // whether the wiring exists; this keeps that claim honest from the web side.
    expect(studioSource).toContain('from "@/components/music/LyricsSectionEditor"');
    expect(studioSource).toContain("<LyricsSectionEditor");
    expect(studioSource).toContain("<MusicLyricsPreflightPanel");
  });

  it("sends no lyrics through the generation request", () => {
    // The submit payload is built from the studio brief only. Attaching lyrics,
    // a language or a dialect here would be a request the capability gate
    // refuses, and it would silently change the release posture of this lane.
    const submitCall = studioSource.slice(studioSource.indexOf("const submitInput"));
    expect(submitCall.slice(0, 900)).not.toMatch(/lyrics[A-Za-z]*:/i);
    expect(studioSource).not.toMatch(/createMusicGenerationJob\([^)]*lyrics/i);
  });

  it("keeps the copy out of the locale catalog until follow-up step 3", () => {
    // No new translation keys are invented here: i18n.ts is one of the most
    // contended files in the repository, and the panel already documents this
    // copy as pending its own lane.
    expect(studioSource).not.toMatch(/t\("music\.lyrics/);
    const labelsSource = readFileSync(new URL("./music/musicStudioLyricsLabels.ts", import.meta.url), "utf8");
    expect(labelsSource).not.toMatch(/from "@\/lib\/i18n"/);
  });

  it("does not reach for a second vocabulary or a second editor", () => {
    const labelsSource = readFileSync(new URL("./music/musicStudioLyricsLabels.ts", import.meta.url), "utf8");
    // The two needles below mirror the exact substrings that
    // MusicLyricsDraftContractMirrorTests scans for across apps/web/src, so
    // this file must not spell them out even while asserting their absence -
    // the bracketed class keeps both the C# `Assert.Empty(declaredIn)` and this
    // expectation meaningful. Keep them bracketed.
    expect(labelsSource).not.toMatch(/const lyrics[Ss]ectionTypes|type Lyrics[Ss]ectionType =/);
    expect(studioSource).not.toMatch(/import \{[^}]*lyricsSectionTypes/);
  });
});
