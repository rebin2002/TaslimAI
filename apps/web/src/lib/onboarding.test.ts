import { describe, expect, it } from "vitest";
import { localeDirection, locales, translate } from "./i18n";
import { onboardingWorkflowDefinitions, onboardingWorkflowFor, shouldShowOnboarding } from "./onboarding";

describe("onboarding workflows", () => {
  it("routes each suggested workflow to a concrete first-run feature", () => {
    expect(onboardingWorkflowDefinitions).toEqual([
      expect.objectContaining({ intent: "project", href: "/projects?create=1" }),
      expect.objectContaining({ intent: "chat", href: "/chat" }),
      expect.objectContaining({ intent: "image", href: "/create/image" }),
      expect.objectContaining({ intent: "document", href: "/create/document" }),
      expect.objectContaining({ intent: "presentation", href: "/create/presentation" }),
      expect.objectContaining({ intent: "research", href: "/create/research" }),
    ]);
  });

  it("resolves the selected workflow without fake destinations", () => {
    expect(onboardingWorkflowFor("project").href).toBe("/projects?create=1");
    expect(onboardingWorkflowFor("research").href).toBe("/create/research");
  });

  it("shows only for a new account and not an existing, completed, or skipped account", () => {
    expect(shouldShowOnboarding({ onboardingCompletedAt: null })).toBe(true);
    expect(shouldShowOnboarding({ onboardingCompletedAt: "2026-09-24T12:00:00.000Z" })).toBe(false);
    expect(shouldShowOnboarding(null)).toBe(false);
  });

  it("localizes first-run copy in every supported interface language with RTL for Arabic and Kurdish", () => {
    for (const locale of locales) {
      expect(translate(locale, "onboarding.preferencesTitle")).not.toBe("onboarding.preferencesTitle");
      expect(translate(locale, "onboarding.workflow.research.description")).not.toBe("onboarding.workflow.research.description");
    }
    expect(localeDirection("en")).toBe("ltr");
    expect(localeDirection("ar")).toBe("rtl");
    expect(localeDirection("ku")).toBe("rtl");
  });
});
