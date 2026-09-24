import { renderToStaticMarkup } from "react-dom/server";
import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({ useAuth: vi.fn(), replace: vi.fn() }));

vi.mock("@/components/AuthProvider", () => ({ useAuth: mocks.useAuth }));
vi.mock("next/navigation", () => ({
  usePathname: () => "/account/admin/operations",
  useRouter: () => ({ replace: mocks.replace }),
}));

import { AdminPage } from "./AdminPage";

describe("AdminPage", () => {
  beforeEach(() => mocks.useAuth.mockReset());

  it("withholds protected page contents from a normal authenticated user", () => {
    mocks.useAuth.mockReturnValue({ user: { isAdmin: false }, loading: false });
    const html = renderToStaticMarkup(<AdminPage><p>Internal provider cost</p></AdminPage>);
    expect(html).toContain("Administrator access required");
    expect(html).not.toContain("Internal provider cost");
  });

  it("renders protected contents only for an administrator capability", () => {
    mocks.useAuth.mockReturnValue({ user: { isAdmin: true }, loading: false });
    const html = renderToStaticMarkup(<AdminPage><p>Internal provider cost</p></AdminPage>);
    expect(html).toContain("Internal provider cost");
  });
});
