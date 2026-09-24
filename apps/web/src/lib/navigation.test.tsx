import { describe, expect, it } from "vitest";
import { routeLabels } from "./data";
import { desktopNavigation, matchesNavigationPath, primaryNavigation, studioCategories, studioRoutes } from "./navigation";

describe("navigation structure", () => {
  it("keeps mobile primary navigation to five focused destinations", () => {
    expect(primaryNavigation.map((item) => item.href)).toEqual(["/", "/projects", "/create", "/notifications", "/account"]);
    expect(primaryNavigation).toHaveLength(5);
    expect(primaryNavigation.map((item) => item.labelKey)).toContain("navigation.notifications");
    expect(primaryNavigation.map((item) => item.labelKey)).not.toContain("navigation.movieStudio");
  });

  it("keeps Chat and utility destinations available on desktop without pinning every Studio", () => {
    expect(desktopNavigation.map((item) => item.href)).toEqual(["/", "/chat", "/projects", "/assets", "/create", "/notifications"]);
    expect(desktopNavigation.map((item) => item.href)).not.toContain("/create/voice");
    expect(matchesNavigationPath("/create/research", "/create")).toBe(true);
    expect(matchesNavigationPath("/chat/conversation-1", "/chat")).toBe(true);
    expect(matchesNavigationPath("/projects", "/")).toBe(false);
  });

  it("maps all current Studios to labeled route destinations", () => {
    const allStudios = studioCategories.flatMap((category) => category.studios);
    expect(allStudios).toHaveLength(8);
    expect(new Set(studioRoutes)).toEqual(new Set(allStudios.map((studio) => studio.href)));
    for (const studio of allStudios) {
      expect(routeLabels[studio.href], `${studio.href} should have a route label`).toBeDefined();
    }
  });

  it("groups the chooser into Media, Work, and Marketing", () => {
    expect(studioCategories.map((category) => category.labelKey)).toEqual([
      "create.category.media",
      "create.category.work",
      "create.category.marketing",
    ]);
    expect(studioRoutes).toContain("/create/image");
    expect(studioRoutes).toContain("/create/social");
  });
});
