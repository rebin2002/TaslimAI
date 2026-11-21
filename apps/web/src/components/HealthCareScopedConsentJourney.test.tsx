import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { healthCareConsentScopeKey } from "./HealthCareScopedConsentJourney";
import { createConsentSelectionStore } from "@/lib/healthCareConsentSelection";

const scopedSource = readFileSync(new URL("./HealthCareScopedConsentJourney.tsx", import.meta.url), "utf8");
const routeSource = readFileSync(new URL("../app/create/health/page.tsx", import.meta.url), "utf8");

describe("Health Care consent identity boundary", () => {
  it("never mounts the consent journey without both a verified user and workspace", () => {
    expect(healthCareConsentScopeKey(null, "workspace-a")).toBeNull();
    expect(healthCareConsentScopeKey("user-a", null)).toBeNull();
    expect(healthCareConsentScopeKey("", "workspace-a")).toBeNull();
    expect(healthCareConsentScopeKey("user-a", "")).toBeNull();
  });

  it("gives both user and workspace identity changes different React remount keys", () => {
    const a = healthCareConsentScopeKey("user-a", "workspace-a");
    expect(a).not.toBeNull();
    expect(a).not.toBe(healthCareConsentScopeKey("user-b", "workspace-a"));
    expect(a).not.toBe(healthCareConsentScopeKey("user-a", "workspace-b"));
    // Unambiguous serialization even when identity text contains punctuation.
    expect(healthCareConsentScopeKey("user:a", "workspace-b")).not.toBe(healthCareConsentScopeKey("user", "a:workspace-b"));
    expect(scopedSource).toContain("key={scope}");
    expect(scopedSource).toContain("loading ? null");
  });

  it("is actually mounted behind the existing server-side auth and noindex route", () => {
    expect(routeSource).toContain('requireAuthenticatedPage("/create/health")');
    expect(routeSource).toContain("<HealthCareScopedConsentJourney />");
    expect(routeSource).toContain("index: false");
    expect(routeSource).toContain("noarchive: true");
  });

  it("a fresh store on an identity transition cannot inherit opt-in or selected rows", () => {
    const accountA = createConsentSelectionStore();
    accountA.grantConsent([{ id: "sample-a", label: "Synthetic A", kind: "note" }]);
    accountA.toggleDocumentSelection("sample-a");
    accountA.requestPreview("sample-a");
    expect(accountA.getSnapshot().preview.status).toBe("granted");

    const accountB = createConsentSelectionStore();
    expect(accountB.getSnapshot().consent).toBe("ungranted");
    expect(accountB.getSnapshot().documents).toEqual([]);
    expect(accountB.getSnapshot().selection).toEqual({});
    expect(accountB.getSnapshot().preview.status).toBe("none");
  });

  it("does not introduce a Health provider, storage, file picker, or medical ingestion", () => {
    for (const token of ["fetch(", "/api/", "sessionStorage", "localStorage", "XMLHttpRequest", 'type="file"', "providerJob"]) {
      expect(scopedSource).not.toContain(token);
    }
  });
});
