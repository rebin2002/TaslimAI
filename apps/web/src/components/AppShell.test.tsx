import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const shellSource = readFileSync(new URL("./AppShell.tsx", import.meta.url), "utf8");

describe("shared app shell accessibility", () => {
  it("places a localized bypass link before the header and targets the main landmark", () => {
    const skipLinkIndex = shellSource.indexOf('className="skip-link"');
    const headerIndex = shellSource.indexOf("<AppHeader");

    expect(skipLinkIndex).toBeGreaterThanOrEqual(0);
    expect(skipLinkIndex).toBeLessThan(headerIndex);
    expect(shellSource).toContain('href="#main-content"');
    expect(shellSource).toContain('t("navigation.skipToContent")');
  });

  it("makes the main landmark programmatically focusable after skip-link activation", () => {
    expect(shellSource).toContain('id="main-content"');
    expect(shellSource).toContain("tabIndex={-1}");
  });
});
