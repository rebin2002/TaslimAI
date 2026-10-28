import { Suspense } from "react";
import { ChatView } from "@/components/ChatView";
import { requireAuthenticatedPage } from "@/lib/serverAuth";

export const dynamic = "force-dynamic";

type ConversationPageProps = { params: Promise<{ conversationId: string }> };

export default async function ConversationPage({ params }: ConversationPageProps) {
  const { conversationId } = await params;
  await requireAuthenticatedPage(`/chat/${conversationId}`);
  return <Suspense fallback={<div className="loading-state"><span className="loading-spinner" /></div>}><ChatView conversationId={conversationId} /></Suspense>;
}
