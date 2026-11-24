import { existsSync, readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { LocaleProvider } from "@/components/LocaleProvider";
import { StudioChooser, healthCareTileHref } from "@/components/StudioChooser";
import { studioCategories } from "@/lib/navigation";

/**
 * Issue #714 follow-up: navigation and privacy truthfulness of the Health Care
 * entry in the mobile Create menu.
 *
 * The original smoke (head 986ce4bf, 2026-11-13) asserted the pre-launch state:
 * the entry had to be "transparently absent", `StudioChooser.tsx` was forbidden
 * from mentioning health at all, and the destination screen was source-scanned
 * for `<form>`, `<input>`, `<textarea>` and `/api/` markers. That shape is now
 * obsolete and preclusive. The chooser lane ships a navigation-only Health Care
 * card and the route lane ships the gated `/create/health` preview, so the
 * absence leg fails on current main 04fcca0da63e
 * (`expect(chooserSource).not.toContain("/create/health")`, measured
 * 1 failed | 8 passed), and `studioCategories` carries no health entry at all,
 * so the old enumeration never even looked at the real menu surface. The
 * collection markers would keep failing the day Health Care grows the
 * functionality it advertises.
 *
 * Truthfulness is still the requirement, so this revision keeps the durable
 * invariants and drops the prohibitions:
 *
 *   1. every Health Care card the mobile menu renders must point at a real,
 *      routable screen on disk - the menu may not advertise a phantom;
 *   2. that screen must actually await its own server gate on this route, must
 *      render the client gate, and must stay unindexed, because the app has no
 *      Next middleware to fall back on;
 *   3. no other health-named link may appear in the mobile menu without
 *      satisfying the same rules;
 *   4. every enabled Create-menu card still resolves to a screen on disk.
 *
 * Nothing here asserts which controls, uploads or provider calls a health
 * screen may eventually contain: that is a product decision, not a privacy
 * invariant. The synthetic fixtures pin that distinction explicitly, and each
 * check is call-shaped rather than a bare marker search, so deleting a gate
 * while leaving its import behind is reported instead of hidden.
 *
 * Test-only. Edits no source file, reads no user or provider data, and touches
 * no path owned by another lane.
 */

type RouteScreenFact = { exists: boolean; source: string };

type HealthMenuLink = {
  href: string;
  /** True when the anchor carries the dedicated health-card marker. */
  fromHealthCard: boolean;
  /** Visible text inside the anchor, used for the localization leg. */
  text: string;
};

/** Next allows either extension for a route segment; accept both. */
const PAGE_FILE_NAMES = ["page.tsx", "page.ts"] as const;

function liveRouteFact(href: string): RouteScreenFact {
  for (const fileName of PAGE_FILE_NAMES) {
    const url = new URL(`../app${href}/${fileName}`, import.meta.url);
    if (existsSync(url)) return { exists: true, source: readFileSync(url, "utf8") };
  }
  return { exists: false, source: "" };
}

/** The awaited authentication gate for this exact route, if the screen has one. */
function awaitedGatePaths(source: string): string[] {
  return [...source.matchAll(/await\s+requireAuthenticatedPage\(\s*["'`]([^"'`]+)["'`]\s*\)/g)].map(
    (match) => match[1],
  );
}

/**
 * Durable truthfulness guard. An advertised health destination is acceptable
 * when the screen exists and keeps its own gates; any other state is either a
 * false capability or an unauthenticated, indexable surface. Deliberately no
 * check on forms, inputs or `/api/` calls: those become legitimate the moment
 * Health Care ships real functionality, and the chooser contract is about not
 * lying about what the screen does, not about freezing it.
 */
function destinationTruthViolations(href: string, fact: RouteScreenFact): string[] {
  const violations: string[] = [];
  if (!fact.exists) {
    violations.push(`${href}: advertised health destination has no routable screen (false capability)`);
    return violations;
  }
  const gates = awaitedGatePaths(fact.source);
  if (gates.length === 0) {
    violations.push(`${href}: route screen never awaits requireAuthenticatedPage, so it is not server-gated`);
  } else if (!gates.some((path) => path === href || path.startsWith(`${href}/`))) {
    violations.push(`${href}: route screen gates ${gates.join(", ")} instead of this route`);
  }
  if (!/<ProtectedPage[\s/>]/.test(fact.source)) {
    violations.push(`${href}: route screen does not render ProtectedPage, so it is not client-gated`);
  }
  if (!/index:\s*false/.test(fact.source)) {
    violations.push(`${href}: route screen does not opt out of indexing and broad publication`);
  }
  return violations;
}

/** Render the mobile (compact) Create menu and read the links it exposes. */
function mobileMenuLinks(): HealthMenuLink[] {
  const html = renderToStaticMarkup(
    createElement(LocaleProvider, null, createElement(StudioChooser, { compact: true })),
  );
  const links: HealthMenuLink[] = [];
  for (const anchor of html.matchAll(/<a\b[^>]*>([\s\S]*?)<\/a>/g)) {
    const tag = anchor[0];
    const href = /href="([^"]+)"/.exec(tag)?.[1];
    if (!href) continue;
    links.push({
      href,
      fromHealthCard: tag.includes('data-health-care-tile="true"'),
      text: anchor[1].replace(/<[^>]*>/g, "").trim(),
    });
  }
  return links;
}

const mobileLinks = mobileMenuLinks();
const healthCardLinks = mobileLinks.filter((link) => link.fromHealthCard);
const healthNamedLinks = mobileLinks.filter(
  (link) => /health/i.test(link.href) || link.fromHealthCard,
);

describe("issue #714 mobile Create menu Health Care truthfulness", () => {
  it("renders exactly one Health Care card pointing at the canonical destination", () => {
    expect(healthCardLinks.length, "the mobile menu must not duplicate the health promise").toBe(1);
    expect(healthCardLinks[0].href).toBe(healthCareTileHref);
    expect(healthNamedLinks.map((link) => link.href)).toEqual([healthCareTileHref]);
  });

  it("advertises only health destinations that are real, gated and unindexed screens", () => {
    const violations = healthNamedLinks.flatMap((link) =>
      destinationTruthViolations(link.href, liveRouteFact(link.href)),
    );
    expect(violations).toEqual([]);
  });

  it("shows localized copy on the Health Care card rather than a raw key", () => {
    const card = healthCardLinks[0];
    expect(card.text.length, "the card must render visible copy").toBeGreaterThan(0);
    expect(card.text, "untranslated navigation key leaked into the menu").not.toMatch(/^navigation\./);
    expect(card.text).not.toMatch(/^create\.studio\./);
  });

  it("routes every enabled Create-menu card to a real screen on disk", () => {
    for (const entry of studioCategories.flatMap((category) => category.studios)) {
      expect(liveRouteFact(entry.href).exists, `${entry.href} must resolve to a routable page`).toBe(true);
    }
  });
});

/**
 * Synthetic fixtures only. Each screen is described by the capability it has,
 * never by fabricated user content, and the builder changes one invariant per
 * fixture so a leg cannot pass because two markers were removed together.
 */
function screenSource(options: {
  serverGate: "thisRoute" | "otherRoute" | "importOnly" | "none";
  clientGate: boolean;
  unindexed: boolean;
  collectsData?: boolean;
}): string {
  const lines = ['import { HealthPlaceholderPage } from "@/components/HealthPlaceholderPage";'];
  if (options.clientGate) lines.push('import { ProtectedPage } from "@/components/ProtectedPage";');
  if (options.serverGate !== "none") lines.push('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
  lines.push('export const dynamic = "force-dynamic";');
  lines.push(options.unindexed ? "    index: false," : "    index: true,");
  if (options.serverGate === "thisRoute") lines.push('  await requireAuthenticatedPage("/create/health");');
  if (options.serverGate === "otherRoute") lines.push('  await requireAuthenticatedPage("/create/image");');
  const wrapper = options.clientGate ? ["<ProtectedPage>", "</ProtectedPage>"] : ["", ""];
  const body = options.collectsData
    ? `  return ${wrapper[0]}<form><input type="file" name="lab-report" />` +
      '<button onClick={() => fetch("/api/health/reports/analyze")}>Analyze</button></form>' +
      `${wrapper[1]};`
    : `  return ${wrapper[0]}<HealthPlaceholderPage entry="create" />${wrapper[1]};`;
  lines.push(body);
  return lines.join("\n");
}

const gatedComingSoon = screenSource({ serverGate: "thisRoute", clientGate: true, unindexed: true });

describe("truthfulness guard discrimination (synthetic fixtures)", () => {
  it("accepts the current navigation-only coming-soon preview screen", () => {
    expect(destinationTruthViolations("/create/health", { exists: true, source: gatedComingSoon })).toEqual([]);
  });

  it("accepts a future health screen that uploads and analyses through the API", () => {
    const future = screenSource({
      serverGate: "thisRoute",
      clientGate: true,
      unindexed: true,
      collectsData: true,
    });
    expect(destinationTruthViolations("/create/health", { exists: true, source: future })).toEqual([]);
  });

  it("rejects a card pointing at a route with no screen on disk", () => {
    const violations = destinationTruthViolations("/create/health-care", { exists: false, source: "" });
    expect(violations.join("\n")).toMatch(/no routable screen \(false capability\)/);
  });

  it("rejects a screen that dropped its server gate", () => {
    const source = screenSource({ serverGate: "none", clientGate: true, unindexed: true });
    const violations = destinationTruthViolations("/create/health", { exists: true, source });
    expect(violations.join("\n")).toMatch(/not server-gated/);
    expect(violations).toHaveLength(1);
  });

  it("rejects a screen that kept the gate import but never awaits it", () => {
    const source = screenSource({ serverGate: "importOnly", clientGate: true, unindexed: true });
    const violations = destinationTruthViolations("/create/health", { exists: true, source });
    expect(violations.join("\n")).toMatch(/not server-gated/);
    expect(violations).toHaveLength(1);
  });

  it("rejects a screen that gates a different route than the one it serves", () => {
    const source = screenSource({ serverGate: "otherRoute", clientGate: true, unindexed: true });
    const violations = destinationTruthViolations("/create/health", { exists: true, source });
    expect(violations.join("\n")).toMatch(/gates \/create\/image instead of this route/);
    expect(violations).toHaveLength(1);
  });

  it("rejects a screen that dropped the client gate", () => {
    const source = screenSource({ serverGate: "thisRoute", clientGate: false, unindexed: true });
    const violations = destinationTruthViolations("/create/health", { exists: true, source });
    expect(violations.join("\n")).toMatch(/not client-gated/);
    expect(violations).toHaveLength(1);
  });

  it("rejects a screen that allowed indexing", () => {
    const source = screenSource({ serverGate: "thisRoute", clientGate: true, unindexed: false });
    const violations = destinationTruthViolations("/create/health", { exists: true, source });
    expect(violations.join("\n")).toMatch(/does not opt out of indexing/);
    expect(violations).toHaveLength(1);
  });

  it("rejects a screen with none of the three invariants", () => {
    const source = screenSource({ serverGate: "none", clientGate: false, unindexed: false });
    expect(destinationTruthViolations("/create/health", { exists: true, source })).toHaveLength(3);
  });
});
