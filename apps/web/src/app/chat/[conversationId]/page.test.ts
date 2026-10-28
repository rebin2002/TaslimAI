import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

const routeSource = readFileSync(new URL("./page.tsx", import.meta.url), "utf8");

describe("chat conversation route authorization", () => {
  it("requires server authentication before rendering the conversation", () => {
    expect(routeSource).toContain('import { requireAuthenticatedPage } from "@/lib/serverAuth";');
    expect(routeSource).toContain('export const dynamic = "force-dynamic";');
    expect(routeSource).toContain("await requireAuthenticatedPage(`/chat/${conversationId}`);");
    const authCallIndex = routeSource.indexOf("await requireAuthenticatedPage");
    const chatViewIndex = routeSource.indexOf("<ChatView conversationId={conversationId} />");
    expect(authCallIndex).toBeGreaterThanOrEqual(0);
    expect(authCallIndex).toBeLessThan(chatViewIndex);
  });
});
