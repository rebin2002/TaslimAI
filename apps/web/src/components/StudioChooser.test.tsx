import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const chooserSource = readFileSync(new URL("./StudioChooser.tsx", import.meta.url), "utf8");
const createPageSource = readFileSync(new URL("../app/create/page.tsx", import.meta.url), "utf8");
const compactChooser = chooserSource.slice(
  chooserSource.indexOf("function CompactStudioLauncher"),
  chooserSource.indexOf("function FullStudioChooser"),
);
const fullChooser = chooserSource.slice(
  chooserSource.indexOf("function FullStudioChooser"),
  chooserSource.indexOf("function StudioCategorySection"),
);

describe("Create page heading structure", () => {
  it("uses a page-level h1 for the full Studio chooser", () => {
    expect(createPageSource).toContain("<StudioChooser />");
    expect(fullChooser).toContain('<h1 id="studio-chooser-title">');
    expect(fullChooser).not.toContain('<h2 id="studio-chooser-title">');
  });

  it("keeps the compact home launcher as a nested section heading", () => {
    expect(compactChooser).toContain('<h2 id="studio-chooser-title">');
    expect(compactChooser).not.toContain('<h1 id="studio-chooser-title">');
  });
});
