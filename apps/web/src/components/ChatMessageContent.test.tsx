import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { ChatMessageContent } from "./ChatMessageContent";

const productionMarkdown = `### Summary

This is a **test summary**.

### 3 important points

1. **First point:** explanation
2. **Second point:** explanation
3. **Third point:** explanation`;

describe("ChatMessageContent production rendering path", () => {
  it("renders a completed streamed assistant response as Markdown", () => {
    const html = renderToStaticMarkup(
      <ChatMessageContent role="Assistant" status="Pending" content={productionMarkdown} />,
    );

    expect(html).toContain("<h3>Summary</h3>");
    expect(html).toContain("<strong>test summary</strong>");
    expect(html).toContain("<ol>");
    expect(html).not.toContain("### Summary");
    expect(html).not.toContain("**test summary**");
  });

  it("renders a persisted assistant response after reload even when role casing differs", () => {
    const html = renderToStaticMarkup(
      <ChatMessageContent role="assistant" status="Completed" content={productionMarkdown} />,
    );

    expect(html).toContain("<h3>3 important points</h3>");
    expect(html).toContain("<strong>First point:</strong>");
    expect(html).toContain("<ol>");
    expect(html).not.toContain("### 3 important points");
    expect(html).not.toContain("**First point:**");
  });

  it("keeps the same response plain for a user message", () => {
    const html = renderToStaticMarkup(
      <ChatMessageContent role="User" status="Completed" content={productionMarkdown} />,
    );

    expect(html).toContain("### Summary");
    expect(html).toContain("**test summary**");
    expect(html).not.toContain("<h3>Summary</h3>");
    expect(html).not.toContain("<strong>test summary</strong>");
  });
});
