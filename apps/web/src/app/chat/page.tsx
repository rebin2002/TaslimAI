import { Suspense } from "react";
import { ChatView } from "@/components/ChatView";

export default function ChatPage() {
  return <Suspense fallback={<div className="loading-state" role="status" aria-live="polite" aria-busy="true"><span className="loading-spinner" aria-hidden="true" /></div>}><ChatView /></Suspense>;
}
