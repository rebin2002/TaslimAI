import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { requireAuthenticatedPage } = vi.hoisted(() => ({
  requireAuthenticatedPage: vi.fn(),
}));

vi.mock("@/lib/serverAuth", () => ({ requireAuthenticatedPage }));
vi.mock("@/components/ActivityCenterView", () => ({
  ActivityCenterView: () => null,
}));
vi.mock("@/components/ProtectedPage", () => ({
  ProtectedPage: ({ children }: { children: ReactNode }) => children,
}));

import ActivityPage from "./page";

describe("Activity route authentication boundary", () => {
  beforeEach(() => {
    requireAuthenticatedPage.mockReset();
    requireAuthenticatedPage.mockResolvedValue(undefined);
  });

  it("checks the session on the server before rendering the private activity surface", async () => {
    await ActivityPage();

    expect(requireAuthenticatedPage).toHaveBeenCalledOnce();
    expect(requireAuthenticatedPage).toHaveBeenCalledWith("/activity");
  });
});
