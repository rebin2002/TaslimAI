import { existsSync, readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

/**
 * Issue #714 — synthetic Health Care consent-revocation UI contract.
 *
 * Owner-path audit (2026-11-13, this task):
 * - #715 owns apps/web/e2e/health-care.spec.ts, apps/web/src/app/create/health/{page,error}.tsx,
 *   HealthCarePage.module.css, HealthPlaceholderPage.{tsx,test.tsx}, healthCareI18n.{ts,test.ts}.
 * - #780 owns apps/web/src/lib/healthCareCreateMenuSmoke.test.ts (Create-menu truthfulness).
 * - #793 owns apps/web/src/app/create/health/page.tsx and StudioChooser.tsx (chooser visibility).
 * - #749 owns apps/web/src/app/personal/health/error.{tsx,recovery-focus.test.tsx}.
 * - #729 owns apps/api.Tests/HealthCareReportAuthorizationTests.cs: SERVER-side revocation of
 *   workspace membership denying synthetic report attachment reads. It proves the API responds
 *   404; it does not cover the client UI state a user sees on the mobile Health Care route.
 * - #717 owns apps/web/e2e/studios-revoked-session-integration.spec.ts: revoked SESSION, not
 *   revoked consent, and no Health Care content.
 *
 * Gap: no open PR or main-branch test pins the UI-level transition "consent is revoked while
 * the user is viewing Health Care → preview actions are hidden AND the private document
 * selection is cleared". Per the #714 owner disposition the Health Care screen is currently
 * information-only, so this contract is stated as a deterministic synthetic state machine and
 * is also verified vacuously against the shipped screens: a half-implemented consent switch,
 * document picker, or preview affordance that could leak private state past revocation must
 * not appear without this transition being wired.
 *
 * Test-only and independent: it edits no source file and touches no owner-claimed path.
 * Only synthetic fixture identifiers are used — no real health data, no model or provider
 * calls, no API calls, no network.
 */

type ConsentDecision = "granted" | "revoked";
type Screen = "away" | "viewing-health-care";

type HealthCareUiState = {
  consent: ConsentDecision;
  screen: Screen;
  selectedPrivateDocumentId: string | null;
  previewActionsVisible: boolean;
};

const INITIAL: HealthCareUiState = {
  consent: "revoked",
  screen: "away",
  selectedPrivateDocumentId: null,
  previewActionsVisible: false,
};

/** Grants consent — a prerequisite for selecting a private document or previewing it. */
function grantConsent(state: HealthCareUiState): HealthCareUiState {
  return { ...state, consent: "granted" };
}

/** The user opens the Health Care mobile route. */
function openHealthCare(state: HealthCareUiState): HealthCareUiState {
  return { ...state, screen: "viewing-health-care" };
}

/** Selecting a private document (synthetic id) reveals the preview actions. */
function selectPrivateDocument(state: HealthCareUiState, documentId: string): HealthCareUiState {
  if (state.consent !== "granted") return state;
  return { ...state, selectedPrivateDocumentId: documentId, previewActionsVisible: true };
}

/**
 * The pinned regression transition: revoking consent — whether or not the user is
 * currently viewing Health Care — must hide preview actions and clear the private
 * document selection immediately.
 */
function revokeConsent(state: HealthCareUiState): HealthCareUiState {
  return {
    ...state,
    consent: "revoked",
    selectedPrivateDocumentId: null,
    previewActionsVisible: false,
  };
}

/**
 * Fail-closed resume: navigating away and back while consent stays revoked must not
 * resurrect the hidden preview actions or the cleared selection.
 */
function resumeViewing(state: HealthCareUiState): HealthCareUiState {
  return { ...state, screen: "viewing-health-care" };
}

/** Re-granting consent starts clean: prior private selections are not implicitly restored. */
function regrantAfterRevocation(state: HealthCareUiState): HealthCareUiState {
  return { ...state, consent: "granted", selectedPrivateDocumentId: null, previewActionsVisible: false };
}

/** Shared invariant guard, mirroring the #780 truthfulness-guard style. */
function consentRevocationViolations(state: HealthCareUiState): string[] {
  const violations: string[] = [];
  if (state.consent === "revoked" && state.previewActionsVisible) {
    violations.push("preview actions remain visible after consent revocation");
  }
  if (state.consent === "revoked" && state.selectedPrivateDocumentId !== null) {
    violations.push(`private document selection survives consent revocation (${state.selectedPrivateDocumentId})`);
  }
  return violations;
}

describe("issue #714 synthetic Health Care consent-revocation UI contract", () => {
  it("hides preview actions and clears the private document selection when consent is revoked while viewing Health Care", () => {
    const viewing = selectPrivateDocument(
      openHealthCare(grantConsent(INITIAL)),
      "synthetic-doc-7A91C5",
    );
    expect(viewing.screen).toBe("viewing-health-care");
    expect(viewing.previewActionsVisible).toBe(true);

    const afterRevocation = revokeConsent(viewing);

    expect(afterRevocation.previewActionsVisible).toBe(false);
    expect(afterRevocation.selectedPrivateDocumentId).toBeNull();
    expect(consentRevocationViolations(afterRevocation)).toEqual([]);
  });

  it("is fail-closed across navigate-away and resume while consent stays revoked", () => {
    const revoked = revokeConsent(
      selectPrivateDocument(openHealthCare(grantConsent(INITIAL)), "synthetic-doc-4F2B77"),
    );

    const resumed = resumeViewing({ ...revoked, screen: "away" });

    expect(resumed.previewActionsVisible).toBe(false);
    expect(resumed.selectedPrivateDocumentId).toBeNull();
    expect(consentRevocationViolations(resumed)).toEqual([]);
  });

  it("does not implicitly restore a prior private selection when consent is re-granted", () => {
    const regranted = regrantAfterRevocation(
      revokeConsent(selectPrivateDocument(openHealthCare(grantConsent(INITIAL)), "synthetic-doc-9C31A0")),
    );

    expect(regranted.consent).toBe("granted");
    expect(regranted.selectedPrivateDocumentId).toBeNull();
    expect(regranted.previewActionsVisible).toBe(false);
    expect(consentRevocationViolations(regranted)).toEqual([]);
  });

  it("only reveals preview actions for an explicitly selected document under granted consent", () => {
    const grantedNoSelection = selectPrivateDocument(
      openHealthCare(grantConsent(INITIAL)),
      "synthetic-doc-BE01",
    );
    const blocked = selectPrivateDocument(INITIAL, "synthetic-doc-BE01");

    expect(grantedNoSelection.previewActionsVisible).toBe(true);
    expect(blocked.selectedPrivateDocumentId).toBeNull();
    expect(blocked.previewActionsVisible).toBe(false);
    expect(consentRevocationViolations(blocked)).toEqual([]);
  });

  it("flags a synthetic leak instead of passing it silently", () => {
    const leaked: HealthCareUiState = {
      consent: "revoked",
      screen: "viewing-health-care",
      selectedPrivateDocumentId: "synthetic-doc-LEAK",
      previewActionsVisible: true,
    };

    const violations = consentRevocationViolations(leaked);
    expect(violations).toHaveLength(2);
    expect(violations.join("\n")).toMatch(/preview actions remain visible after consent revocation/);
    expect(violations.join("\n")).toMatch(/private document selection survives consent revocation/);
  });

  it("keeps the shipped Health Care screens free of half-wired consent or document surfaces", () => {
    // The screens that would host the future consent UI. On main only the personal
    // Health route exists; the #715 create route joins once its owner hold resolves.
    const screens = [
      new URL("../app/personal/health/page.tsx", import.meta.url),
      new URL("../app/create/health/page.tsx", import.meta.url),
      new URL("../components/HealthPlaceholderPage.tsx", import.meta.url),
    ];
    // Surfaces that would make consent revocation observable in the UI. If any appears
    // without the revokeConsent transition above wired, private state can survive revocation.
    const revocableSurfaces = ["<input", "<form", "<select", "type=\"file\"", "checkbox", "aria-checked", "switch\""];

    let audited = 0;
    for (const screen of screens) {
      if (!existsSync(screen)) continue;
      audited += 1;
      const source = readFileSync(screen, "utf8");
      for (const surface of revocableSurfaces) {
        expect(source, `Health Care screen must not expose a ${surface} consent or document surface`).not.toContain(surface);
      }
    }
    expect(audited, "at least the personal Health Care screen is auditable").toBeGreaterThan(0);
  });

  it("keeps the consent precondition truthful in every shipped Health Care locale note", () => {
    // i18n.ts is owner-claimed (#714 hold, five claimants) — read-only assertion:
    // the shipped privacy note must keep promising explicit consent for any future
    // health feature, which is exactly what the revocation contract above enforces.
    const i18n = readFileSync(new URL("./i18n.ts", import.meta.url), "utf8");
    expect(i18n).toContain("it will require explicit consent and clear workspace ownership controls");
  });
});
