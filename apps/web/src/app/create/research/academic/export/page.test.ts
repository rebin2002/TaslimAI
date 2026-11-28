import { readFileSync } from "node:fs";
import { describe, expect, it, vi } from "vitest";

/**
 * Issue #1267 (export slice) — the login gate must promise back the export the reader asked for.
 *
 * `/create/research/academic/export?job=<id>` is the deep link the generation-jobs page and the
 * template gallery both hand out, and `AcademicReportExportPanel` selects that report from the query
 * on first render. A reader whose session has expired therefore has to come back to the same report;
 * if the gate is handed only the pathname, sign-in returns them to a blank export screen and the link
 * they followed is gone. That is the defect this file pins, in both directions: the query survives, and
 * nothing about surviving it can widen the destination beyond this origin.
 */
const JOB = "1f2e3d4c-b5a6-4789-abcd-0123456789ab";
const ROUTE = "/create/research/academic/export";

vi.mock("next/headers", () => ({ cookies: vi.fn() }));
vi.mock("next/navigation", () => ({
  redirect: vi.fn((destination: string) => {
    throw new Error(`REDIRECT:${destination}`);
  }),
}));
// The private surface itself is not under test here, and importing it would drag the API client and
// the whole panel tree into a test about the gate. Both are replaced by inert stubs that record.
vi.mock("@/components/ProtectedPage", () => ({ ProtectedPage: () => null }));
vi.mock("@/components/academic/templates/AcademicReportExportPanel", () => ({ AcademicReportExportPanel: () => null }));

const { redirect } = await import("next/navigation");
const { cookies } = await import("next/headers");
const pageModule = await import("./page");
const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

/** Renders the gate with no session cookie at all: the cheapest expired-session state there is. */
async function gateFor(searchParams: Record<string, string | string[] | undefined>) {
  vi.mocked(redirect).mockClear();
  vi.mocked(cookies).mockResolvedValue({ get: () => undefined } as never);
  let caught = "";
  try {
    await pageModule.default({ searchParams: Promise.resolve(searchParams) });
  } catch (error) {
    caught = (error as Error).message;
  }
  expect(caught).toMatch(/^REDIRECT:/);
  return caught.slice("REDIRECT:".length);
}

describe("export deep link through the login gate", () => {
  it("returns to the same saved report after sign-in", async () => {
    const next = await gateFor({ job: JOB });
    expect(next).toBe(`/login?next=${encodeURIComponent(`${ROUTE}?job=${JOB}`)}`);
    // The full location, not the pathname: this is the whole point of the change.
    expect(decodeURIComponent(next)).toContain(`job=${JOB}`);
    expect(next).not.toBe(`/login?next=${encodeURIComponent(ROUTE)}`);
  });

  it("keeps every occurrence of a repeated parameter, in order", async () => {
    const next = await gateFor({ job: [JOB, "00000000-0000-4000-8000-000000000000"] });
    const returned = decodeURIComponent(next.replace("/login?next=", ""));
    expect(returned).toBe(`${ROUTE}?job=${JOB}&job=00000000-0000-4000-8000-000000000000`);
  });

  it("holds an unsafe job value inside this origin instead of dropping the gate", async () => {
    // A value that is not a job id is still the reader's requested location: it is preserved for
    // sign-in, percent-encoded so a separator cannot become structure, and never used as a
    // destination on its own. The panel validates the id again before selecting anything.
    for (const hostile of ["//evil.example/x", "https://evil.example/", "..%2f..%2flogin", '"><img src=x>', "javascript:alert(1)"]) {
      const next = await gateFor({ job: hostile });
      expect(next.startsWith("/login?next=")).toBe(true);
      const returned = decodeURIComponent(next.replace("/login?next=", ""));
      expect(returned.startsWith(ROUTE)).toBe(true);
      // Nothing in the return path may be re-spelled as an absolute or protocol-relative location.
      expect(returned.slice(ROUTE.length)).toContain("%");
      expect(returned).not.toMatch(/^\/\//);
    }
  });

  it("falls back to the pathname alone when there is no job to carry", async () => {
    expect(await gateFor({})).toBe(`/login?next=${encodeURIComponent(ROUTE)}`);
    expect(await gateFor({ job: "" })).toBe(`/login?next=${encodeURIComponent(`${ROUTE}?job=`)}`);
  });

  it("reads the query before the gate, which is the ordering that makes the promise possible", () => {
    const awaitQuery = source.indexOf("const query = await searchParams;");
    const gate = source.indexOf("await requireAuthenticatedPage(internalReturnPath(academicExportRoute, query));");
    expect(awaitQuery).toBeGreaterThan(-1);
    expect(gate).toBeGreaterThan(-1);
    expect(awaitQuery).toBeLessThan(gate);
    expect(source).toContain('import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('import { isAcademicResearchVisible } from "@/lib/academicResearchVisibility";');
    expect(source).toContain('import { notFound } from "next/navigation";');
    const visibilityGate = source.indexOf("if (!isAcademicResearchVisible()) notFound();");
    expect(visibilityGate).toBeGreaterThan(gate);
  });

  it("still validates the identifier before handing it to the panel", () => {
    const validate = source.indexOf("const initialJobId =");
    const gate = source.indexOf("await requireAuthenticatedPage(");
    expect(validate).toBeGreaterThan(-1);
    expect(gate).toBeLessThan(validate);
    expect(source).toContain("export const dynamic = \"force-dynamic\";");
  });
});
