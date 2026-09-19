"use client";

import { FormEvent, KeyboardEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Archive, Check, Edit3, MessageCircle, MoreHorizontal, Plus, Search, Send, Sparkles, X } from "lucide-react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { ApiError, api, type ChatMessage, type Conversation } from "@/lib/api";
import { createChatStreamState, reduceChatStream } from "@/lib/chatStreamState";
import { claimSubmission, conversationPath, createSubmission, releaseSubmission, shouldReplaceConversationUrl } from "@/lib/chatLifecycle";
import { ProtectedPage } from "@/components/ProtectedPage";

type ChatViewProps = { conversationId?: string };
type ConversationGroup = { label: string; items: Conversation[] };
type RetryRequest = { conversationId: string; content: string; requestId: string };

function dateGroup(date: string, now = new Date()) {
  const value = new Date(date);
  const startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const startValue = new Date(value.getFullYear(), value.getMonth(), value.getDate());
  const days = Math.round((startToday.getTime() - startValue.getTime()) / 86_400_000);
  if (days <= 0) return "today";
  if (days === 1) return "yesterday";
  if (days <= 7) return "week";
  return "older";
}

export function ChatView({ conversationId }: Readonly<ChatViewProps>) {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const router = useRouter();
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [selected, setSelected] = useState<Conversation | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [search, setSearch] = useState("");
  const [content, setContent] = useState("");
  const [loading, setLoading] = useState(true);
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState("");
  const [renaming, setRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState("");
  const [retryRequest, setRetryRequest] = useState<RetryRequest | null>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const sendingRef = useRef(false);

  const loadConversations = useCallback(async () => {
    if (!workspace) return;
    const items = await api.listConversations(workspace.id);
    setConversations(items);
    return items;
  }, [workspace]);

  useEffect(() => {
    let active = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoading(true);
    setError("");
    void (async () => {
      try {
        const items = await loadConversations();
        if (!active) return;
        if (conversationId) {
          const [conversation, history] = await Promise.all([api.getConversation(conversationId), api.getMessages(conversationId)]);
          if (!active) return;
          setSelected(conversation);
          setMessages(history);
          if (!items?.some(item => item.id === conversation.id)) setConversations(current => [conversation, ...current]);
        } else {
          setSelected(null);
          setMessages([]);
        }
      } catch (caught) {
        if (active) setError(caught instanceof ApiError && caught.status === 404 ? t("chat.notFound") : t("chat.loadError"));
      } finally {
        if (active) setLoading(false);
      }
    })();
    return () => { active = false; };
  }, [conversationId, loadConversations, t]);

  useEffect(() => { messagesEndRef.current?.scrollIntoView({ behavior: "smooth" }); }, [messages, generating]);

  const filteredConversations = useMemo(() => {
    const query = search.trim().toLowerCase();
    return conversations.filter(item => !query || item.title.toLowerCase().includes(query));
  }, [conversations, search]);

  const groups = useMemo<ConversationGroup[]>(() => {
    const labels: Record<string, string> = { today: t("chat.today"), yesterday: t("chat.yesterday"), week: t("chat.previousWeek"), older: t("chat.older") };
    return ["today", "yesterday", "week", "older"].map(key => ({ label: labels[key], items: filteredConversations.filter(item => dateGroup(item.updatedAt) === key) })).filter(group => group.items.length > 0);
  }, [filteredConversations, t]);

  function selectConversation(item: Conversation) {
    if (generating) return;
    router.push(conversationPath(item.id));
  }

  function startNewChat() {
    if (sendingRef.current) return;
    setError("");
    setRetryRequest(null);
    setSelected(null);
    setMessages([]);
    setContent("");
    setRenaming(false);
    router.push("/chat");
  }

  async function send(event?: FormEvent | KeyboardEvent, retry?: RetryRequest) {
    event?.preventDefault();
    const text = retry?.content ?? content.trim();
    if (!text || generating || text.length > 20000 || !workspace) return;
    if (!claimSubmission(sendingRef)) return;
    const submission = createSubmission(content.trim(), selected?.id, retry);
    const { requestId: id } = submission;
    let activeConversationId = submission.conversationId;
    setError("");
    setRetryRequest(null);
    setGenerating(true);
    try {
      let conversation = selected;
      if (!conversation) {
        conversation = await api.createConversation(workspace.id);
        activeConversationId = conversation.id;
        setSelected(conversation);
        setConversations(current => [conversation!, ...current]);
      }
      let streamState = createChatStreamState(messages);
      await api.streamMessage(conversation.id, text, streamEvent => {
        streamState = reduceChatStream(streamState, streamEvent);
        setMessages(streamState.messages);
        setGenerating(streamState.generating);
        if (streamEvent.type === "message.started" || streamEvent.type === "message.completed") {
          if (streamEvent.data.conversation) {
            setSelected(streamEvent.data.conversation);
            setConversations(current => [streamEvent.data.conversation!, ...current.filter(item => item.id !== streamEvent.data.conversation!.id)]);
          }
          if (streamEvent.type === "message.completed") setContent("");
        } else if (streamEvent.type === "message.failed") {
          setRetryRequest({ conversationId: conversation!.id, content: text, requestId: id });
          setError(streamEvent.data.code === "CONVERSATION_ARCHIVED" ? t("chat.archivedError") : t("chat.generationError"));
        }
      }, id);
      if (conversation && shouldReplaceConversationUrl(conversationId, conversation.id)) {
        window.history.replaceState(window.history.state, "", conversationPath(conversation.id));
      }
      setGenerating(false);
    } catch (caught) {
      setGenerating(false);
      if (activeConversationId) setRetryRequest({ conversationId: activeConversationId, content: text, requestId: id });
      setError(caught instanceof ApiError && caught.code === "CONVERSATION_ARCHIVED" ? t("chat.archivedError") : t("chat.generationError"));
    } finally {
      releaseSubmission(sendingRef);
    }
  }

  async function saveRename() {
    if (!selected || !renameValue.trim()) return;
    try {
      const updated = await api.renameConversation(selected.id, renameValue.trim());
      setSelected(updated);
      setConversations(current => current.map(item => item.id === updated.id ? updated : item));
      setRenaming(false);
    } catch (caught) { setError(caught instanceof Error ? caught.message : t("chat.renameError")); }
  }

  async function archiveSelected() {
    if (!selected) return;
    try {
      await api.archiveConversation(selected.id);
      setConversations(current => current.filter(item => item.id !== selected.id));
      startNewChat();
    } catch (caught) { setError(caught instanceof Error ? caught.message : t("chat.archiveError")); }
  }

  return <ProtectedPage><div className="chat-page">
    <aside className="chat-sidebar">
      <div className="chat-sidebar-header"><div><p className="section-eyebrow">{t("chat.eyebrow")}</p><h1>{t("chat.title")}</h1></div><button type="button" className="chat-icon-button" onClick={startNewChat} aria-label={t("chat.newChat")}><Plus size={18} /></button></div>
      <button type="button" className="chat-new-button" onClick={startNewChat} disabled={generating}><Plus size={15} />{t("chat.newChat")}</button>
      <label className="chat-search"><Search size={15} /><input value={search} onChange={event => setSearch(event.target.value)} placeholder={t("chat.searchPlaceholder")} aria-label={t("chat.searchPlaceholder")} /></label>
      <div className="chat-history" aria-label={t("chat.history")}>
        {loading ? <div className="chat-sidebar-loading"><span className="loading-spinner" /></div> : groups.length === 0 ? <p className="chat-sidebar-empty">{t("chat.noConversations")}</p> : groups.map(group => <section key={group.label} className="chat-history-group"><span className="chat-history-label">{group.label}</span>{group.items.map(item => <button type="button" key={item.id} className={`chat-history-item ${selected?.id === item.id ? "is-active" : ""}`} onClick={() => selectConversation(item)} disabled={generating}><MessageCircle size={14} /><span>{item.title}</span></button>)}</section>)}
      </div>
      <div className="chat-sidebar-footer"><Sparkles size={15} /><span>{t("chat.providerNotice")}</span></div>
    </aside>
    <main className="chat-main">
      <header className="chat-main-header"><div className="chat-title-block"><span className="chat-header-icon"><MessageCircle size={18} /></span><div><p className="section-eyebrow">{t("chat.headerEyebrow")}</p><h2>{selected?.title ?? t("chat.newChat")}</h2></div></div>{selected && <div className="chat-header-actions">{renaming ? <div className="chat-rename"><input value={renameValue} onChange={event => setRenameValue(event.target.value)} onKeyDown={event => { if (event.key === "Enter") void saveRename(); }} autoFocus /><button type="button" onClick={() => void saveRename()} aria-label={t("chat.saveRename")}><Check size={15} /></button><button type="button" onClick={() => setRenaming(false)} aria-label={t("common.cancel")}><X size={15} /></button></div> : <><button type="button" className="chat-header-action" onClick={() => { setRenameValue(selected.title); setRenaming(true); }}><Edit3 size={14} />{t("chat.rename")}</button><button type="button" className="chat-header-action is-danger" onClick={() => void archiveSelected()}><Archive size={14} />{t("chat.archive")}</button></>}</div>}</header>
      <div className="chat-messages" aria-live="polite">
        {loading ? <div className="chat-empty"><span className="loading-spinner" /></div> : error && !selected ? <div className="chat-empty"><CircleMessage /><h3>{error}</h3><button type="button" className="secondary-button" onClick={startNewChat}>{t("chat.newChat")}</button></div> : messages.length === 0 ? <div className="chat-empty"><span className="chat-empty-icon"><Sparkles size={22} /></span><h3>{t("chat.emptyTitle")}</h3><p>{t("chat.emptyDescription")}</p></div> : <>{messages.map(message => <article className={`chat-message chat-message-${message.role.toLowerCase()} ${message.status === "Failed" ? "is-failed" : ""}`} key={message.id}><div className="chat-message-avatar">{message.role === "User" ? "T" : <Sparkles size={15} />}</div><div className="chat-message-copy"><span className="chat-message-role">{message.role === "User" ? t("chat.you") : t("chat.taslim")}</span>{message.status === "Pending" && !message.content ? <div className="chat-typing"><i /><i /><i /></div> : <p>{message.content}</p>}{message.status === "Failed" && retryRequest && <button type="button" className="chat-retry-button" onClick={() => void send(undefined, retryRequest)} disabled={generating}><Send size={13} />{t("chat.retry")}</button>}{message.isTestResponse && <small className="chat-test-badge">{t("chat.testResponse")}</small>}</div></article>)}<div ref={messagesEndRef} /></>}
      </div>
      {error && selected && <div className="chat-inline-error" role="alert">{error}{retryRequest && <button type="button" className="chat-inline-retry" onClick={() => void send(undefined, retryRequest)} disabled={generating}>{t("chat.retry")}</button>}</div>}
      <form className="chat-composer" onSubmit={send}><textarea value={content} onChange={event => setContent(event.target.value)} onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey) void send(event); }} placeholder={t("chat.composerPlaceholder")} maxLength={20000} disabled={generating} aria-label={t("chat.composerPlaceholder")} /><div className="chat-composer-footer"><span>{t("chat.composerHint")}</span><button type="submit" className="chat-send-button" disabled={generating || !content.trim()} aria-label={t("chat.send")}><Send size={16} /></button></div></form>
    </main>
  </div></ProtectedPage>;
}

function CircleMessage() { return <span className="chat-empty-icon"><MoreHorizontal size={22} /></span>; }
