import { readFileSync } from "node:fs";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { decideMusicStudioSubmit, type MusicStudioBriefDraft } from "@/lib/musicStudioPreflight";
import { MusicGenerationAction, MusicStudioView } from "./MusicStudioView";

vi.mock("next/navigation", () => ({
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => ({ workspace: { id: "workspace-1" } }),
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

const translate = (key: string) => key;

// A brief that satisfies every rule the studio itself states: long enough
// description and purpose, a workspace, no reference selection, nothing running.
const readyBrief: MusicStudioBriefDraft = {
  workspaceReady: true,
  submitting: false,
  referenceSelected: false,
  description: "A calm cinematic bed for a two minute explainer",
  purpose: "Background music for a product walkthrough",
};

function renderAction(draft: MusicStudioBriefDraft): string {
  return renderToStaticMarkup(
    <MusicGenerationAction decision={decideMusicStudioSubmit(draft)} working={draft.submitting} noticeId="music-preflight-note" t={translate} />,
  );
}

function studioMarkup(): string {
  // Server rendering runs no effects: this is exactly what a visitor to
  // /create/music is shown before any workspace request settles.
  return renderToStaticMarkup(<MusicStudioView />);
}

function buttonTag(markup: string): string {
  const start = markup.indexOf('class="music-generate-button');
  expect(start, "the primary action must be rendered").toBeGreaterThan(-1);
  return markup.slice(markup.lastIndexOf("<", start), markup.indexOf(">", start) + 1);
}

describe("Music Studio provider-unavailable preflight on /create/music", () => {
  it("states that creation is paused before a brief is offered", () => {
    const html = studioMarkup();
    expect(html).toContain('id="music-preflight-note"');
    expect(html).toContain("music-studio-notice");
    expect(html).toContain("music.providerUnavailableTitle");
    expect(html).toContain("music.providerUnavailableText");
  });

  it("disables the generation action for a complete brief while creation is paused", () => {
    // Reproduction: the page told the visitor, in all three launch locales, that
    // the music service is not configured and that no track would be created, yet
    // a complete brief armed a live primary action whose only outcome was a
    // rejected POST of the user's private brief.
    expect(renderAction(readyBrief)).toContain('disabled=""');
    expect(renderAction(readyBrief)).toContain('aria-disabled="true"');
    expect(renderAction(readyBrief)).toContain("is-paused");
  });

  it("explains the disabled action instead of leaving it silently inert", () => {
    const tag = buttonTag(renderAction(readyBrief));
    // A silently dead button is a dead end for keyboard and screen-reader users.
    expect(tag).toContain('aria-describedby="music-preflight-note"');
    expect(tag).toContain('title="music.providerUnavailableText"');
    expect(studioMarkup()).toContain('role="status"');
  });

  it("opens the same action once the studio is open, without a stale pause reason", () => {
    const tag = buttonTag(renderAction({ ...readyBrief, access: "open" }));
    expect(tag).not.toContain("disabled");
    expect(tag).not.toContain("aria-describedby");
    expect(tag).not.toContain("is-paused");
    expect(tag).not.toContain("music.providerUnavailableText");
    // Incomplete briefs still gate, but only on the user's own input.
    expect(buttonTag(renderAction({ ...readyBrief, access: "open", description: "ab" }))).toContain('disabled=""');
    expect(buttonTag(renderAction({ ...readyBrief, access: "open", description: "ab" }))).not.toContain("is-paused");
  });

  it("keeps the brief itself editable while creation is paused", () => {
    const html = studioMarkup();
    // The published copy promises the brief "can stay here until it is
    // available", so the fields must not be disabled along with the action.
    const textareas = html.match(/<textarea[^>]*>/g);
    expect(textareas && textareas.length > 0).toBe(true);
    for (const textarea of textareas!) {
      expect(textarea).not.toContain("disabled");
    }
    expect(html).toContain("music.descriptionLabel");
    expect(html).toContain("music.purpose");
  });

  it("never names or reaches a provider from the paused studio", () => {
    expect(studioMarkup()).not.toMatch(/suno|mubert|stable[- ]?audio/i);
    expect(studioMarkup()).not.toContain("/api/music-generation");
    // The refusal is decided before the transport call appears in the handler.
    const source = readFileSync(new URL("./MusicStudioView.tsx", import.meta.url), "utf8");
    const decision = source.indexOf("decideMusicStudioSubmit(");
    const submit = source.indexOf("api.createMusicGenerationJob(");
    expect(decision, "the submit path must consult the preflight decision").toBeGreaterThan(-1);
    expect(submit).toBeGreaterThan(-1);
    expect(decision).toBeLessThan(submit);
    expect(source).toContain("if (!submitDecision.permitted)");
  });
});
