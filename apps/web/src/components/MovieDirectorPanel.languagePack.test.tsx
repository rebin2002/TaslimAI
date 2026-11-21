import { renderToStaticMarkup } from "react-dom/server";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MovieProject } from "@/lib/api";
import { MovieDirectorPanel } from "./MovieDirectorPanel";

const { translate } = vi.hoisted(() => ({
  translate: vi.fn((key: string) => key),
}));

vi.mock("@/components/LocaleProvider", () => ({
  useLocale: () => ({ locale: "en", t: translate }),
}));

const project = {
  id: "synthetic-project",
  title: "Synthetic Project",
  guide: { lockedRevisionNumber: null, currentRevisionNumber: null },
  scenes: [],
  clips: [],
  characters: [],
  locations: [],
} as unknown as MovieProject;

describe("MovieDirectorPanel language-pack integration", () => {
  beforeEach(() => {
    translate.mockImplementation((key) => key === "movieDirector.text.title" ? "Synthetic Film Director" : key);
  });

  it("uses the provider translator for visible Movie Director copy without provider or API calls", () => {
    const markup = renderToStaticMarkup(
      <MovieDirectorPanel
        project={project}
        activeModule="overview"
        selectedScene={null}
        selectedShot={null}
        onProjectRefresh={async () => undefined}
      />,
    );

    expect(markup).toContain("Synthetic Film Director");
    expect(translate).toHaveBeenCalledWith("movieDirector.text.title", undefined);
  });
});
