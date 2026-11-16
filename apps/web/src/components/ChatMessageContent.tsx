import type { ChatMessage } from "../lib/api";
import { AssistantMarkdown } from "./AssistantMarkdown";

type ChatMessageContentProps = Readonly<{
  role: ChatMessage["role"] | string;
  status: ChatMessage["status"];
  content: string;
}>;

export function ChatMessageContent({ role, status, content }: ChatMessageContentProps) {
  if (status === "Pending" && !content) {
    return <div className="chat-typing"><i /><i /><i /></div>;
  }

  return role.toLowerCase() === "assistant"
    ? <AssistantMarkdown content={content} />
    : <p>{content}</p>;
}
