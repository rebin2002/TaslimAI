import { renderToStaticMarkup } from "react-dom/server";
import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  listProjects: vi.fn(),
  listFiles: vi.fn(),
  listAssets: vi.fn(),
}));

vi.mock("react", async () => {
  const actual = await vi.importActual<typeof import("react")>("react");
  return {
    ...actual,
    useCallback: (callback: unknown) => callback,
    useEffect: (effect: () => unknown) => { void effect(); },
    useRef: (value: unknown) => ({ current: value }),
    useState: (initial: unknown) => [typeof initial === "function" ? (initial as () => unknown)() : initial, vi.fn()],
  };
});

vi.mock("next/navigation", () => ({ useSearchParams: () => new URLSearchParams() }));
vi.mock("next/link", () => ({
  default: ({ href, children, className }: { href: string; children: import("react").ReactNode; className?: string }) =>
    <a href={href} className={className}>{children}</a>,
}));
vi.mock("@/components/AuthProvider", () => ({ useAuth: () => ({ workspace: { id: "workspace-1" } }) }));
vi.mock("@/components/LocaleProvider", () => ({ useLocale: () => ({ locale: "en", t: (key: string) => key }) }));
vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error { status = 500; },
  api: new Proxy({}, { get: (_target, property: string) => property === "listProjects" ? mocks.listProjects : property === "listFiles" ? mocks.listFiles : property === "listAssets" ? mocks.listAssets : vi.fn() }),
}));

import { ResearchStudioView } from "./ResearchStudioView";

describe("Research Studio project selection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.listProjects.mockImplementation((_workspaceId: string, status: string) => Promise.resolve(status === "Archived"
      ? [{ id: "archived-1", name: "Archived research", status: "Archived" }]
      : [{ id: "active-1", name: "Active research", status: "Active" }]));
    mocks.listFiles.mockResolvedValue([]);
    mocks.listAssets.mockResolvedValue({ items: [] });
  });

  it("does not load archived projects into a new research report selector", async () => {
    ResearchStudioView();
    await Promise.resolve();
    await Promise.resolve();

    expect(mocks.listProjects).toHaveBeenCalledWith("workspace-1", "Active");
    expect(mocks.listProjects).not.toHaveBeenCalledWith("workspace-1", "Archived");
  });

  it("keeps the Standard Research form and adds a reachable Academic Research entry", () => {
    const markup = renderToStaticMarkup(ResearchStudioView());

    expect(markup).toContain('href="/create/research/academic"');
    expect(markup).toContain('class="research-studio-layout"');
    expect(markup).toContain('type="submit"');
    expect(markup).toContain("research.generate");
  });
});
