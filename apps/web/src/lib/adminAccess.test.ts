import { describe, expect, it } from "vitest";
import { adminPageAccess } from "./adminAccess";

describe("admin page access", () => {
  it("never treats a normal authenticated user as authorized for an admin page", () => {
    expect(adminPageAccess(false, true, false)).toBe("forbidden");
  });

  it("distinguishes loading, unauthenticated, and active administrator states", () => {
    expect(adminPageAccess(true, false, false)).toBe("loading");
    expect(adminPageAccess(false, false, false)).toBe("unauthenticated");
    expect(adminPageAccess(false, true, true)).toBe("allowed");
  });
});
