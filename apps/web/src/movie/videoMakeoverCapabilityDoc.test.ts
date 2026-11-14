import { readFileSync } from "node:fs";

import { describe, expect, it } from "vitest";

/**
 * Issue #698 architecture-spike guard.
 *
 * This test reads only the checked-in capability document. It makes no network
 * request, calls no provider, and asserts nothing about runtime behavior. Its
 * purpose is narrow: the AI Video Makeover spike must stay an honest,
 * provider-neutral architecture artifact, and every capability row it contains
 * must keep carrying an explicit evidence label and a citable official source.
 * If a future edit tries to smuggle an unverified provider guarantee into the
 * document as if it were verified, this test fails before the claim can ship.
 */

const document = readFileSync(
  new URL("../../../../docs/movie-video-makeover-capabilities.md", import.meta.url),
  "utf8",
);

const REQUIRED_HEADINGS = [
  "## Evidence labels",
  "## Non-negotiable acceptance rules",
  "## Source preservation architecture",
  "## Feature capability matrix",
  "## Per-route verified capability and unknowns matrix",
  "## Cost, protection, and privacy gates",
  "## Phased implementation matrix",
  "## Verification follow-ups",
  "## File ownership check",
  "## Sources consulted",
];

const EVIDENCE_LABELS = ["V", "C", "U", "N"] as const;

const ALLOWED_DOC_HOSTS = [
  "docs.dev.runwayml.com",
  "runwayml.com",
  "help.runwayml.com",
  "dev.runwayml.com",
  "kling.ai",
  "docs.byteplus.com",
  "docs.volcengine.com",
  "ai.google.dev",
  "cloud.google.com",
  "docs.cloud.google.com",
  "docs.lumalabs.ai",
  "lumalabs.ai",
];

function tableRows(heading: string): string[][] {
  const lines = document.split("\n");
  const start = lines.findIndex((line) => line.trim() === heading);
  expect(start, `missing section: ${heading}`).toBeGreaterThanOrEqual(0);

  const rows: string[][] = [];
  for (let index = start + 1; index < lines.length; index += 1) {
    const line = lines[index].trim();
    if (line.startsWith("#")) break;
    if (!line.startsWith("|")) continue;
    if (line.includes("---")) continue;
    const cells = line
      .slice(1, line.endsWith("|") ? -1 : undefined)
      .split("|")
      .map((cell) => cell.trim());
    if (cells[0].toLowerCase() === "route" || cells[0].toLowerCase() === "feature") continue;
    rows.push(cells);
  }
  return rows;
}

describe("movie video makeover capability spike document", () => {
  it("keeps the spike explicitly non-activating", () => {
    expect(document).toContain("No provider is enabled");
    expect(document).toContain("no paid model call");
    expect(document).toContain("Verification date for all external claims: 2026-11-12");
    expect(document).not.toMatch(/provider is enabled for production/i);
    expect(document).not.toMatch(/live capability/i);
  });

  it("documents every required acceptance, capability, cost, and phase section", () => {
    for (const heading of REQUIRED_HEADINGS) {
      expect(document, `missing heading ${heading}`).toContain(heading);
    }
  });

  it("labels every route row with a known evidence label", () => {
    const rows = tableRows("## Per-route verified capability and unknowns matrix");
    expect(rows.length).toBeGreaterThanOrEqual(6);
    for (const row of rows) {
      expect(row).toHaveLength(5);
      const labels = row.filter((cell) =>
        EVIDENCE_LABELS.some((label) =>
          new RegExp("^" + String.raw`\`` + label + String.raw`(|[^A-Za-z])`).test(cell),
        ),
      );
      expect(labels.length, `route row without evidence label: ${row[0]}`).toBeGreaterThan(0);
    }
  });

  it("records the four unverified guarantees as unresolved rather than verified", () => {
    const summary = document.match(
      /No reviewed route is verified for[\s\S]{0,400}/,
    );
    expect(summary, "missing unverified-guarantee summary").not.toBeNull();
    for (const guarantee of [
      "`identity-preservation`",
      "`speech-preservation`",
      "`temporal-continuity`",
      "`frame-rate-preservation`",
    ]) {
      expect(summary?.[0], `unverified summary must list ${guarantee}`).toContain(guarantee);
    }
    expect(summary?.[0]).not.toContain("`V`");
  });

  it("labels every feature row so no capability can be claimed silently", () => {
    const rows = tableRows("## Feature capability matrix");
    expect(rows.length).toBeGreaterThanOrEqual(6);
    for (const row of rows) {
      expect(row).toHaveLength(5);
      const evidence = row[2] ?? "";
      expect(evidence, `feature row without evidence label: ${row[0]}`).toMatch(
        /`[VCUN]\b/,
      );
    }
  });

  it("cites only official provider or Google documentation domains", () => {
    const urls = [...document.matchAll(/https?:\/\/[^\s)>]+/g)].map((match) => match[0]);
    expect(urls.length).toBeGreaterThanOrEqual(10);
    for (const url of urls) {
      const host = new URL(url).host;
      expect(
        ALLOWED_DOC_HOSTS.some((allowed) => host === allowed || host.endsWith(`.${allowed}`)),
        `unexpected citation host: ${host}`,
      ).toBe(true);
    }
  });

  it("requires each route row to carry at least one source URL", () => {
    const rows = tableRows("## Per-route verified capability and unknowns matrix");
    for (const row of rows) {
      const evidence = row[4] ?? "";
      expect(evidence, `route row without a source link: ${row[0]}`).toContain("http");
    }
  });

  it("keeps wardrobe work evidence-gated in a later phase", () => {
    const rows = tableRows("## Phased implementation matrix");
    const wardrobe = rows.find((row) => row.join(" ").includes("Wardrobe Transform"));
    expect(wardrobe, "wardrobe phase is missing").toBeDefined();
    expect(wardrobe?.join(" ")).toContain("consent");
    const wardrobeFeature = tableRows("## Feature capability matrix").find((row) =>
      row[0].includes("wardrobe-transform"),
    );
    expect(wardrobeFeature?.[2], "wardrobe must be labelled as a vendor claim").toMatch(/^`C`/);
    expect(wardrobeFeature?.[2]).not.toContain("`V`");
  });

  it("keeps the spike scoped to its own two files", () => {
    expect(document).toContain("docs/movie-video-makeover-capabilities.md");
    expect(document).toContain("apps/web/src/movie/videoMakeoverCapabilityDoc.test.ts");
    for (const ownedByOtherPr of [
      "apps/api/Movies/MovieEntities.cs",
      "apps/api/Movies/MovieFinalAssembly.cs",
      "apps/web/src/components/MovieStudioView.tsx",
    ]) {
      const lines = document
        .split("\n")
        .filter((line) => line.includes(ownedByOtherPr) && line.startsWith("1.") === false);
      for (const line of lines) {
        expect(line, `out-of-scope file mentioned without ownership note: ${line}`).toMatch(
          /open PR|owned by|not modified|untouched|deliberately left unmodified/,
        );
      }
    }
  });

  it("describes the proposed capability vocabulary as absent from today's registry", () => {
    const today = document.match(/Today's kinds are [\s\S]{0,200}/);
    expect(today, "registry baseline statement is missing").not.toBeNull();
    expect(today?.[0]).toContain("video-generation");
    expect(today?.[0]).not.toContain("video-editing");
    for (const key of [
      "edit-in-place",
      "source-audio-passthrough",
      "identity-preserve",
      "wardrobe-transform",
    ]) {
      expect(document, `proposed capability key ${key} is not documented`).toContain(key);
    }
  });
});
