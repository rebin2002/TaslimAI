import { existsSync, readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const source = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("private memory route", () => {
  it("renders personal memory behind both server and client authentication gates", () => {
    expect(source).toContain('import { ProtectedPage } from "@/components/ProtectedPage";');
    expect(source).toContain('import { internalReturnPath, requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(source).toContain('export const dynamic = "force-dynamic";');
    expect(source).toContain('await requireAuthenticatedPage(internalReturnPath("/account/memory", query));');
    expect(source).toContain("<ProtectedPage><MemoryView /></ProtectedPage>");
  });

  it("promises back the requested location rather than only the pathname", () => {
    // The client boundary this gate fronts returns pathname + search, so the
    // server gate must not narrow that promised destination.
    expect(source).toContain("const query = await searchParams;");
    expect(source.indexOf("const query = await searchParams;")).toBeLessThan(
      source.indexOf('await requireAuthenticatedPage(internalReturnPath("/account/memory", query));'),
    );
  });

  it("keeps the gate visible to the server renderer", () => {
    // This route used to carry a "use client" layout. A client layout receives
    // the page as a `children` prop, so a redirect() thrown by the page below it
    // is reported to the nearest error boundary (src/app/error.tsx) instead of
    // becoming a navigation: anonymous and forged-cookie requests rendered a 200
    // error page rather than the 307 the gate promises. The session-revalidation
    // boundary therefore renders from inside the page, under the gate, with the
    // same wrapper order it had as a layout.
    expect(existsSync(new URL("./layout.tsx", import.meta.url))).toBe(false);
    const boundary = readFileSync(new URL("./session-boundary.tsx", import.meta.url), "utf8");
    expect(boundary.startsWith('"use client";')).toBe(true);
    expect(source).toContain("<MemorySessionBoundary><ProtectedPage><MemoryView /></ProtectedPage></MemorySessionBoundary>");
    expect(source.indexOf("requireAuthenticatedPage(")).toBeLessThan(source.indexOf("<MemorySessionBoundary>"));
  });
});
