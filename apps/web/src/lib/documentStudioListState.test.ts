/**
 * Provider-free pins for the Document Studio documents-list states.
 *
 * Scope: the decision each rail makes between still loading, failed, genuinely
 * empty and populated, plus the completeness of the strings behind those four
 * states in every supported locale. The document renderer, job/draft
 * restoration and export state are separate lanes and are not touched here.
 * No browser, no network, no provider: everything is a pure function of the
 * settled read outcome and the number of items on screen.
 */
import { describe, expect, it } from "vitest";
import {
  documentStudioListLoad,
  documentStudioListStatus,
  type DocumentStudioListLoad,
} from "@/lib/documentStudioListState";
import { locales, translate } from "@/lib/i18n";

const listStateKeys = [
  "document.sourcesLoadError", "document.sourcesLoadErrorHint", "document.retrySources",
  "document.recentLoadError", "document.recentLoadErrorHint", "document.retryRecent",
] as const;

function settled<T>(value: T): PromiseSettledResult<T> { return { status: "fulfilled", value }; }
function rejected(): PromiseSettledResult<never> { return { status: "rejected", reason: new Error("list read failed") }; }

describe("Document Studio documents-list state mapping", () => {
  it("maps each settled read outcome to its own rail load state", () => {
    expect(documentStudioListLoad(settled([]))).toBe("ready");
    expect(documentStudioListLoad(settled({ items: [] }))).toBe("ready");
    expect(documentStudioListLoad(rejected())).toBe("error");
  });

  it("keeps loading, failure and a true empty list apart when there is nothing to show", () => {
    const loads: DocumentStudioListLoad[] = ["loading", "error", "ready"];
    expect(loads.map((load) => documentStudioListStatus(load, 0))).toEqual(["loading", "error", "empty"]);
  });

  it("keeps listing documents that are already on screen whatever the next read does", () => {
    for (const load of ["loading", "ready", "error"] as const) {
      expect(documentStudioListStatus(load, 1)).toBe("populated");
      expect(documentStudioListStatus(load, 4)).toBe("populated");
    }
  });

  it("never resolves a failed read to the empty state", () => {
    expect(documentStudioListStatus("error", 0)).not.toBe("empty");
    expect(documentStudioListStatus("error", 0)).toBe("error");
  });
});

describe("Document Studio documents-list strings", () => {
  it("localizes every load-state string in every supported locale", () => {
    for (const locale of locales) {
      for (const key of listStateKeys) {
        const value = translate(locale, key);
        expect(value, `${locale}:${key}`).toBeTruthy();
        expect(value, `${locale}:${key}`).not.toBe(key);
      }
    }
  });

  it("translates every new message into Arabic and Kurdish rather than mirroring English", () => {
    for (const key of listStateKeys) {
      expect(translate("ar", key)).not.toBe(translate("en", key));
      expect(translate("ku", key)).not.toBe(translate("en", key));
    }
  });

  it("says something different for a failure than for a genuinely empty list", () => {
    for (const locale of locales) {
      expect(translate(locale, "document.recentLoadError")).not.toBe(translate(locale, "document.recentEmpty"));
      expect(translate(locale, "document.sourcesLoadError")).not.toBe(translate(locale, "document.noSources"));
    }
  });

  it("keeps the retry affordance an action the member can take", () => {
    for (const locale of locales) {
      expect(translate(locale, "document.retryRecent")).toBeTruthy();
      expect(translate(locale, "document.retrySources")).toBeTruthy();
    }
  });
});
