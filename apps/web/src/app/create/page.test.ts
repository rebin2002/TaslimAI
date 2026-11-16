import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const pageSource = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("create route authentication boundary", () => {
  it("checks the session before rendering the private Studio chooser", () => {
    expect(pageSource).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(pageSource).toContain('export const dynamic = "force-dynamic";');
    expect(pageSource).toContain("export default async function CreatePage()");
    expect(pageSource).toContain('await requireAuthenticatedPage("/create");');
    expect(pageSource).toContain("<StudioChooser />");
  });
});
