import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const homeSource = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");
const studioSource = readFileSync(new URL("../components/StudioChooser.tsx", import.meta.url), "utf8");

describe("authenticated Home structure", () => {
  it("keeps one primary creation surface and no duplicate Chat CTA", () => {
    expect(homeSource.match(/<HomeComposer/g)).toHaveLength(1);
    expect(homeSource).toContain("home-creation-surface");
    expect(homeSource).not.toContain("Open Taslim Chat");
    expect(homeSource).not.toContain("home.openChatDirect");
    expect(homeSource).not.toContain("home.chatCta");
  });

  it("keeps the compact Studio rail and real-data Continue section", () => {
    expect(homeSource).toContain("<StudioChooser compact />");
    expect(homeSource).toContain("home.continueSectionTitle");
    expect(homeSource).toContain("buildHomeRecentItems");
    expect(studioSource).toContain("studio-chooser-compact");
    expect(studioSource).toContain("href=\"/create\"");
  });

  it("does not move mobile navigation or Studio routing into Home content", () => {
    expect(homeSource).toContain("href=\"/activity\"");
    expect(studioSource).toContain("studioCategories.flatMap");
    expect(studioSource).toContain("href={studio.href}");
  });
});
