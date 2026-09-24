import { Suspense } from "react";
import { ChatView } from "@/components/ChatView";

export default function ChatPage() {
  return <Suspense fallback={<div className="loading-state"><span className="loading-spinner" /></div>}><ChatView /></Suspense>;
}
