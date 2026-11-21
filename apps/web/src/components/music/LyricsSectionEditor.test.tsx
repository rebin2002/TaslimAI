import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import {
  LYRICS_MAX_CHARS_PER_SECTION,
  LYRICS_MAX_SECTIONS,
  LyricsSectionEditor,
  addLyricsSection,
  lyricsInputDirection,
  lyricsInputLanguageTag,
  lyricsSectionAriaLabel,
  lyricsSectionsErrorMessage,
  moveLyricsSection,
  normalizeLyricsSectionText,
  normalizeLyricsSections,
  removeLyricsSection,
  setLyricsSectionText,
  validateLyricsSections,
  type LyricsSection,
  type LyricsSectionEditorLabels,
} from "./LyricsSectionEditor";

const editorSource = readFileSync(new URL("./LyricsSectionEditor.tsx", import.meta.url), "utf8");

const labels: LyricsSectionEditorLabels = {
  sectionType: (type, ordinal) => `${type[0].toUpperCase()}${type.slice(1)} ${ordinal}`,
  addSection: (type) => `Add ${type}`,
  removeSection: (ordinal) => `Remove section ${ordinal}`,
  moveUp: (ordinal) => `Move section ${ordinal} up`,
  moveDown: (ordinal) => `Move section ${ordinal} down`,
  lyricInput: "Write lyrics for this section",
  charactersLimit: (max) => `Maximum ${max} characters per section`,
  sectionsLimit: (max) => `Maximum ${max} sections`,
  emptySectionError: "Every section needs lyrics before you continue.",
  tooLongError: (max) => `Sections cannot exceed ${max} characters.`,
  tooManySectionsError: (max) => `You can add at most ${max} sections.`,
};

const baseSections: LyricsSection[] = [
  { id: "s1", type: "verse", text: "Bist dengê çiyayan" },
  { id: "s2", type: "chorus", text: "ئەڵێم بگەڕێمەوە" },
  { id: "s3", type: "verse", text: "Rêya kurdî dirêj e" },
];

describe("LyricsSectionEditor structure helpers", () => {
  it("adds a blank section of the requested type without mutating the input", () => {
    const original = baseSections.map((section) => ({ ...section }));
    const next = addLyricsSection(baseSections, "bridge", "b1");
    expect(next).toHaveLength(4);
    expect(next[3]).toEqual({ id: "b1", type: "bridge", text: "" });
    expect(baseSections).toEqual(original);
  });

  it("removes only the targeted section", () => {
    const next = removeLyricsSection(baseSections, "s2");
    expect(next.map((section) => section.id)).toEqual(["s1", "s3"]);
  });

  it("reorders with relative offsets and absolute targets, ignoring out-of-range moves", () => {
    expect(moveLyricsSection(baseSections, "s3", { offset: -1 }).map((s) => s.id)).toEqual(["s1", "s3", "s2"]);
    expect(moveLyricsSection(baseSections, "s1", { targetIndex: 2 }).map((s) => s.id)).toEqual(["s2", "s3", "s1"]);
    expect(moveLyricsSection(baseSections, "s1", { offset: -1 }).map((s) => s.id)).toEqual(["s1", "s2", "s3"]);
    expect(moveLyricsSection(baseSections, "s3", { offset: 1 }).map((s) => s.id)).toEqual(["s1", "s2", "s3"]);
    expect(moveLyricsSection(baseSections, "missing", { offset: 1 }).map((s) => s.id)).toEqual(["s1", "s2", "s3"]);
  });

  it("edits section text as a controlled update keyed by section id", () => {
    const next = setLyricsSectionText(baseSections, "s2", "new chorus line");
    expect(next.find((section) => section.id === "s2")?.text).toBe("new chorus line");
    expect(baseSections.find((section) => section.id === "s2")?.text).toBe("ئەڵێم بگەڕێمەوە");
  });
});

describe("LyricsSectionEditor validation", () => {
  it("trims whitespace before enforcing emptiness and length limits", () => {
    expect(validateLyricsSections([{ id: "a", type: "verse", text: "   \n\t  " }])).toEqual([
      { code: "section_empty", sectionId: "a", params: { sectionOrdinal: 1 } },
    ]);
    const long = "x".repeat(LYRICS_MAX_CHARS_PER_SECTION + 5) + "   ";
    const errors = validateLyricsSections([{ id: "a", type: "chorus", text: long }]);
    expect(errors).toEqual([
      {
        code: "section_too_long",
        sectionId: "a",
        params: { sectionOrdinal: 1, maxCharsPerSection: LYRICS_MAX_CHARS_PER_SECTION, actual: LYRICS_MAX_CHARS_PER_SECTION + 5 },
      },
    ]);
  });

  it("honours custom limits and flags an over-long list", () => {
    const errors = validateLyricsSections([{ id: "a", type: "verse", text: "hello world" }], { maxCharsPerSection: 5 });
    expect(errors[0]?.code).toBe("section_too_long");
    const tooMany = Array.from({ length: LYRICS_MAX_SECTIONS + 1 }, (_, index) => ({
      id: `s${index}`,
      type: "verse" as const,
      text: "line",
    }));
    expect(validateLyricsSections(tooMany)).toEqual([{ code: "sections_too_many", params: { maxSections: LYRICS_MAX_SECTIONS } }]);
  });

  it("rejects unknown section types and missing arrays", () => {
    expect(validateLyricsSections([])).toEqual([]);
    expect(validateLyricsSections([{ id: "a", type: "hook" as LyricsSection["type"], text: "x" }])[0]?.code).toBe(
      "section_type_invalid",
    );
    expect(validateLyricsSections(undefined)).toEqual([{ code: "sections_missing", params: {} }]);
  });

  it("normalizes draft text deterministically for submission", () => {
    expect(normalizeLyricsSectionText("  satır \r\n\n\n\n\n  end  ")).toBe("satır\n\nend");
    const next = normalizeLyricsSections([{ id: "a", type: "verse", text: "  a  \n" }]);
    expect(next[0]).toEqual({ id: "a", type: "verse", text: "a" });
  });

  it("maps every error code to consumer-supplied localized copy", () => {
    expect(lyricsSectionsErrorMessage({ code: "section_empty", params: {} }, labels)).toBe(labels.emptySectionError);
    expect(lyricsSectionsErrorMessage({ code: "section_too_long", params: { maxCharsPerSection: 999 } }, labels)).toBe(
      "Sections cannot exceed 999 characters.",
    );
    expect(lyricsSectionsErrorMessage({ code: "sections_too_many", params: { maxSections: 8 } }, labels)).toBe(
      "You can add at most 8 sections.",
    );
  });
});

describe("LyricsSectionEditor direction handling", () => {
  it("binds Sorani Kurdish and Arabic to rtl, English to ltr, unknown codes to auto", () => {
    expect(lyricsInputDirection("ku")).toBe("rtl");
    expect(lyricsInputDirection("ku-Arab")).toBe("rtl");
    expect(lyricsInputDirection("ckb")).toBe("rtl");
    expect(lyricsInputDirection("ar")).toBe("rtl");
    expect(lyricsInputDirection("ar-EG")).toBe("rtl");
    expect(lyricsInputDirection("en")).toBe("ltr");
    expect(lyricsInputDirection("en-US")).toBe("ltr");
    expect(lyricsInputDirection("zh-CN")).toBe("auto");
    expect(lyricsInputDirection("")).toBe("auto");
  });

  it("exposes Sorani with the ckb language tag for assistive tech", () => {
    expect(lyricsInputLanguageTag("ku")).toBe("ckb");
    expect(lyricsInputLanguageTag("ar")).toBe("ar");
    expect(lyricsInputLanguageTag("en")).toBe("en");
  });

  it("renders each textarea with the bound dir and lang attributes", () => {
    const html = renderToStaticMarkup(
      <LyricsSectionEditor value={baseSections} onChange={vi.fn()} labels={labels} language="ku" />,
    );
    expect(html).toContain('dir="rtl"');
    expect(html).toContain('lang="ckb"');
    expect(html).not.toContain('dir="ltr"');
  });
});

describe("LyricsSectionEditor markup and accessibility", () => {
  it("is a controlled component with accessible add, remove and reorder buttons", () => {
    const onChange = vi.fn();
    const html = renderToStaticMarkup(
      <LyricsSectionEditor value={baseSections} onChange={onChange} labels={labels} language="en" />,
    );
    expect(html).toContain('aria-label="Move section 1 up"');
    // First row cannot move up and last row cannot move down.
    expect(html.match(/disabled[^>]*aria-label="Move section \d up"/g)?.length).toBe(1);
    expect(html.match(/disabled[^>]*aria-label="Move section \d down"/g)?.length).toBe(1);
    expect(html).toContain('aria-label="Remove section 2"');
    expect(html).toContain('aria-label="Add bridge"');
    expect(html).toContain(lyricsSectionAriaLabel(labels, "verse", 1));
    expect(html).toContain('dir="ltr"');
  });

  it("announces per-section validation errors via role=alert and aria-invalid", () => {
    const html = renderToStaticMarkup(
      <LyricsSectionEditor
        value={[{ id: "s1", type: "verse", text: "   " }]}
        onChange={vi.fn()}
        labels={labels}
        language="ar"
      />,
    );
    expect(html).toContain('aria-invalid="true"');
    expect(html).toContain('role="alert"');
    expect(html).toContain(labels.emptySectionError);
  });

  it("keeps the add controls reachable but reports the section cap in the hints", () => {
    const atLimit = Array.from({ length: LYRICS_MAX_SECTIONS }, (_, index) => ({
      id: `s${index}`,
      type: "verse" as const,
      text: "line",
    }));
    const html = renderToStaticMarkup(
      <LyricsSectionEditor value={atLimit} onChange={vi.fn()} labels={labels} language="en" />,
    );
    expect(html).toContain(labels.sectionsLimit(LYRICS_MAX_SECTIONS));
    expect(html).toContain(labels.charactersLimit(LYRICS_MAX_CHARS_PER_SECTION));
  });

  it("hides every mutation control when allowEditing is false", () => {
    const html = renderToStaticMarkup(
      <LyricsSectionEditor
        value={baseSections}
        onChange={vi.fn()}
        labels={labels}
        language="en"
        allowEditing={false}
      />,
    );
    expect(html).not.toContain('aria-label="Add verse"');
    expect(html).not.toContain('aria-label="Remove section 1"');
  });
});

describe("LyricsSectionEditor scope guard", () => {
  it("is standalone: no provider, API, storage or generation references", () => {
    expect(editorSource).not.toMatch(/suno|mubert|stable[- _]?audio|lyria|provider/i);
    expect(editorSource).not.toMatch(/\bfetch\(|XMLHttpRequest|api\.|\/api\//);
    expect(editorSource).not.toMatch(/import .*from .*["']@\/lib\/(api|i18n)["']/);
    expect(editorSource).not.toMatch(/generateLyrics|generateSong|startGeneration|createGenerationJob/);
  });

  it("does not auto-generate content: added sections always start empty", () => {
    const next = addLyricsSection(baseSections, "outro", "o1");
    expect(next[next.length - 1]?.text).toBe("");
  });
});
