import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

describe("Social Studio route authentication boundary", () => {
  it("checks the session on the server before rendering the client studio", () => {
    const route = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

    expect(route).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(route).toContain('export const dynamic = "force-dynamic";');
    expect(route).toContain('await requireAuthenticatedPage("/create/social");');
    expect(route).toContain("export default async function SocialPage()");
  });
});
