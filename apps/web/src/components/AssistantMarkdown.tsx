import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";

type AssistantMarkdownProps = Readonly<{ content: string }>;

export function safeMarkdownHref(href: string | undefined): string | undefined {
  if (!href) return undefined;
  const value = href.trim();
  if (!value) return undefined;
  if (value.startsWith("/") || value.startsWith("./") || value.startsWith("../") || value.startsWith("#")) return value;

  try {
    const url = new URL(value);
    return ["http:", "https:", "mailto:"].includes(url.protocol) ? value : undefined;
  } catch {
    return undefined;
  }
}

const components: Components = {
  a: ({ node, href, children, ...props }) => {
    void node;
    const safeHref = safeMarkdownHref(href);
    if (!safeHref) return <span {...props}>{children}</span>;
    return <a {...props} href={safeHref} target="_blank" rel="noopener noreferrer nofollow">{children}</a>;
  },
  table: ({ node, children, ...props }) => {
    void node;
    return <div className="chat-markdown-table"><table {...props}>{children}</table></div>;
  },
};

export function AssistantMarkdown({ content }: AssistantMarkdownProps) {
  return <div className="chat-markdown"><ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>{content}</ReactMarkdown></div>;
}
