import { Suspense } from "react";
import { ChatView } from "@/components/ChatView";

type ConversationPageProps = { params: Promise<{ conversationId: string }> };

export default async function ConversationPage({ params }: ConversationPageProps) {
  const { conversationId } = await params;
  return <Suspense fallback={<div className="loading-state" role="status" aria-live="polite" aria-busy="true"><span className="loading-spinner" aria-hidden="true" /></div>}><ChatView conversationId={conversationId} /></Suspense>;
}
