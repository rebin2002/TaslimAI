import { ChatView } from "@/components/ChatView";

type ConversationPageProps = { params: Promise<{ conversationId: string }> };

export default async function ConversationPage({ params }: ConversationPageProps) {
  const { conversationId } = await params;
  return <ChatView conversationId={conversationId} />;
}
