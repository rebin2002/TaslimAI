import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { AssistantMarkdown, isExternalMarkdownHref, safeMarkdownHref } from "./AssistantMarkdown";

describe("AssistantMarkdown", () => {
  it("renders common Markdown and GFM structures without raw HTML", () => {
    const html = renderToStaticMarkup(
      <AssistantMarkdown content={`### Summary

**Bold** and *italic*.

- first
  - nested

> quoted

| Item | Value |
| --- | --- |
| A | B |

\`inline\`\n\n\`\`\`ts\nconst answer = 42;\n\`\`\`

<script>alert('unsafe')</script>`} />,
    );

    expect(html).toContain("<h3>Summary</h3>");
    expect(html).toContain("<strong>Bold</strong>");
    expect(html).toContain("<em>italic</em>");
    expect(html).toContain("<table>");
    expect(html).toContain("<blockquote>");
    expect(html).toContain("<code>inline</code>");
    expect(html).toContain("const answer = 42;");
    expect(html).not.toContain("<script>");
  });

  it("allows only safe link protocols and adds safe external-link attributes", () => {
    expect(safeMarkdownHref("https://example.com/docs")).toBe("https://example.com/docs");
    expect(safeMarkdownHref("mailto:team@example.com")).toBe("mailto:team@example.com");
    expect(safeMarkdownHref("/projects/project-1")).toBe("/projects/project-1");
    expect(safeMarkdownHref("#sources")).toBe("#sources");
    expect(safeMarkdownHref("//evil.example/redirect")).toBeUndefined();
    expect(safeMarkdownHref("javascript:alert(1)")).toBeUndefined();
    expect(safeMarkdownHref("data:text/html,<script>alert(1)</script>")).toBeUndefined();
    expect(isExternalMarkdownHref("https://example.com/docs")).toBe(true);
    expect(isExternalMarkdownHref("mailto:team@example.com")).toBe(true);
    expect(isExternalMarkdownHref("/projects/project-1")).toBe(false);

    const html = renderToStaticMarkup(<AssistantMarkdown content="[Docs](https://example.com/docs) [Project](/projects/project-1) [Unsafe](javascript:alert(1)) [Open redirect](//evil.example/redirect)" />);
    expect(html).toContain('href="https://example.com/docs"');
    expect(html).toContain('target="_blank"');
    expect(html).toContain('rel="noopener noreferrer nofollow"');
    expect(html).toContain('href="/projects/project-1"');
    expect(html).not.toContain('href="/projects/project-1" target="_blank"');
    expect(html).not.toContain('href="javascript:alert(1)"');
    expect(html).not.toContain('href="//evil.example/redirect"');
  });
});
