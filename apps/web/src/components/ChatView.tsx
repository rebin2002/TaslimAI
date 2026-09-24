"use client";

import { FormEvent, KeyboardEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Archive, Check, Copy, Edit3, FileText, Image, MessageCircle, MoreHorizontal, Paperclip, Plus, Presentation, RefreshCw, Search, Send, Sparkles, Square, Trash2, X } from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { ApiError, api, type ChatMessage, type Conversation, type Project, type StoredFile } from "@/lib/api";
import { applyChatStreamEvent, createChatStreamState, stopChatStream } from "@/lib/chatStreamState";
import { claimSubmission, conversationPath, createChatRequestId, createSubmission, isAbortError, releaseSubmission, shouldReplaceConversationUrl, studioTransitionPath } from "@/lib/chatLifecycle";
import { ProtectedPage } from "@/components/ProtectedPage";
import { ChatMessageContent } from "@/components/ChatMessageContent";

type ChatViewProps = { conversationId?: string };
type ConversationGroup = { label: string; items: Conversation[] };
type RetryRequest = { conversationId: string; content: string; requestId: string; attachmentIds: string[] };
type UploadProgress = { complete: number; total: number; fileName: string } | null;
type Creator = "document" | "presentation" | "research" | "image" | "social";

const creatorActions: { id: Creator; label: string; icon: typeof FileText }[] = [
  { id: "document", label: "chat.createDocument", icon: FileText },
  { id: "presentation", label: "chat.createPresentation", icon: Presentation },
  { id: "research", label: "chat.createResearch", icon: Search },
  { id: "image", label: "chat.createImage", icon: Image },
  { id: "social", label: "chat.createSocial", icon: Sparkles },
];

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

function formatFileSize(sizeBytes: number) {
  if (sizeBytes < 1024 * 1024) return `${Math.max(1, Math.round(sizeBytes / 1024))} KB`;
  return `${(sizeBytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function ChatView({ conversationId }: Readonly<ChatViewProps>) {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const router = useRouter();
  const searchParams = useSearchParams();
  const requestedProjectId = searchParams.get("projectId") ?? "";
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [projects, setProjects] = useState<Project[]>([]);
  const [selected, setSelected] = useState<Conversation | null>(null);
  const [selectedProjectId, setSelectedProjectId] = useState("");
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [search, setSearch] = useState("");
  const [content, setContent] = useState("");
  const [loading, setLoading] = useState(true);
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState("");
  const [renaming, setRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState("");
  const [retryRequest, setRetryRequest] = useState<RetryRequest | null>(null);
  const [attachments, setAttachments] = useState<StoredFile[]>([]);
  const [uploadProgress, setUploadProgress] = useState<UploadProgress>(null);
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false);
  const [copiedMessageId, setCopiedMessageId] = useState<string | null>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const sendingRef = useRef(false);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const streamAbortRef = useRef<AbortController | null>(null);
  const activeRetryRef = useRef<RetryRequest | null>(null);

  const selectedProject = useMemo(() => projects.find((project) => project.id === (selected?.projectId ?? selectedProjectId)) ?? null, [projects, selected?.projectId, selectedProjectId]);

  const loadConversations = useCallback(async () => {
    if (!workspace) return;
    const items = await api.listConversations(workspace.id);
    setConversations(items);
    return items;
  }, [workspace]);

  useEffect(() => {
    if (!workspace) return;
    let active = true;
    void api.listProjects(workspace.id, "Active")
      .then((items) => { if (active) setProjects(items); })
      .catch(() => { if (active) setProjects([]); });
    return () => { active = false; };
  }, [workspace]);

  useEffect(() => {
    let active = true;
    // Loading persisted conversation state is an external synchronization.
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
          setSelectedProjectId(conversation.projectId ?? "");
          setMessages(history);
          if (!items?.some(item => item.id === conversation.id)) setConversations(current => [conversation, ...current]);
        } else {
          setSelected(null);
          setSelectedProjectId(requestedProjectId);
          setMessages([]);
        }
      } catch (caught) {
        if (active) setError(caught instanceof ApiError && caught.status === 404 ? t("chat.notFound") : t("chat.loadError"));
      } finally {
        if (active) setLoading(false);
      }
    })();
    return () => { active = false; };
  }, [conversationId, loadConversations, requestedProjectId, t]);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: generating ? "auto" : "smooth", block: "end" });
  }, [messages, generating]);

  useEffect(() => () => streamAbortRef.current?.abort(), []);

  const filteredConversations = useMemo(() => {
    const query = search.trim().toLowerCase();
    return conversations.filter(item => !query || item.title.toLowerCase().includes(query));
  }, [conversations, search]);

  const groups = useMemo<ConversationGroup[]>(() => {
    const labels: Record<string, string> = { today: t("chat.today"), yesterday: t("chat.yesterday"), week: t("chat.previousWeek"), older: t("chat.older") };
    return ["today", "yesterday", "week", "older"].map(key => ({ label: labels[key], items: filteredConversations.filter(item => dateGroup(item.updatedAt) === key) })).filter(group => group.items.length > 0);
  }, [filteredConversations, t]);

  const canRegenerate = useCallback((message: ChatMessage) => {
    const latestAssistant = [...messages].reverse().find((item) => item.role === "Assistant" && item.status === "Completed");
    return !!selected && !generating && message.role === "Assistant" && message.status === "Completed" && latestAssistant?.id === message.id;
  }, [generating, messages, selected]);

  function selectConversation(item: Conversation) {
    if (generating) return;
    setError("");
    setRetryRequest(null);
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
    setAttachments([]);
    setSelectedProjectId(requestedProjectId);
    router.push(requestedProjectId ? `/chat?projectId=${encodeURIComponent(requestedProjectId)}` : "/chat");
  }

  async function uploadSelectedFiles(files: FileList | null) {
    if (!files || !workspace) return;
    const queue = Array.from(files);
    setUploadProgress({ complete: 0, total: queue.length, fileName: queue[0]?.name ?? "" });
    setError("");
    try {
      for (const [index, file] of queue.entries()) {
        setUploadProgress({ complete: index, total: queue.length, fileName: file.name });
        // Chat uploads are scoped only to the uploader until they are explicitly attached to this message.
        const stored = await api.uploadFile(workspace.id, file);
        setAttachments(current => [...current, stored]);
        setUploadProgress({ complete: index + 1, total: queue.length, fileName: file.name });
      }
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("chat.attachmentError"));
    } finally {
      setUploadProgress(null);
      if (fileInputRef.current) fileInputRef.current.value = "";
    }
  }

  async function removeAttachment(file: StoredFile) {
    setAttachments(current => current.filter(item => item.id !== file.id));
    try {
      await api.deleteFile(file.id);
    } catch {
      setError(t("chat.attachmentRemoveError"));
    }
  }

  async function clearAttachments() {
    const pending = attachments;
    setAttachments([]);
    await Promise.all(pending.map(async (file) => {
      try { await api.deleteFile(file.id); } catch { /* cleared locally; the safe server cleanup may be retried later */ }
    }));
  }

  function applyStreamEvent(streamEvent: ReturnType<typeof createChatStreamState> extends never ? never : Parameters<typeof applyChatStreamEvent>[1], streamState: ReturnType<typeof createChatStreamState>) {
    return applyChatStreamEvent(streamState, streamEvent, next => {
      setMessages(next.messages);
      setGenerating(next.generating);
    });
  }

  function updateConversationFromStream(streamEvent: { type: string; data: { conversation?: Conversation } }) {
    if ((streamEvent.type === "message.started" || streamEvent.type === "message.completed") && streamEvent.data.conversation) {
      setSelected(streamEvent.data.conversation);
      setSelectedProjectId(streamEvent.data.conversation.projectId ?? "");
      setConversations(current => [streamEvent.data.conversation!, ...current.filter(item => item.id !== streamEvent.data.conversation!.id)]);
    }
  }

  async function send(event?: FormEvent | KeyboardEvent, retry?: RetryRequest) {
    event?.preventDefault();
    const text = retry?.content ?? content.trim();
    if (!text || generating || uploadProgress || text.length > 20000 || !workspace) return;
    if (!claimSubmission(sendingRef)) return;
    const submission = createSubmission(text, selected?.id, retry, attachments.map(file => file.id));
    const { requestId: id } = submission;
    const attachmentIds = submission.attachmentIds;
    let activeConversationId = submission.conversationId;
    const controller = new AbortController();
    streamAbortRef.current = controller;
    setError("");
    setRetryRequest(null);
    setGenerating(true);
    try {
      let conversation = selected;
      if (!conversation) {
        conversation = await api.createConversation(workspace.id, { projectId: selectedProjectId || undefined });
        activeConversationId = conversation.id;
        setSelected(conversation);
        setConversations(current => [conversation!, ...current]);
      }
      activeRetryRef.current = { conversationId: conversation.id, content: text, requestId: id, attachmentIds };
      let streamState = createChatStreamState(messages);
      await api.streamMessage(conversation.id, text, streamEvent => {
        streamState = applyStreamEvent(streamEvent, streamState);
        updateConversationFromStream(streamEvent);
        if (streamEvent.type === "message.completed") {
          setContent("");
          setAttachments([]);
        } else if (streamEvent.type === "message.failed") {
          setRetryRequest({ conversationId: conversation!.id, content: text, requestId: id, attachmentIds });
          setError(streamEvent.data.code === "CONVERSATION_ARCHIVED" ? t("chat.archivedError") : t("chat.generationError"));
        }
      }, id, attachmentIds, controller.signal);
      if (conversation && shouldReplaceConversationUrl(conversationId, conversation.id)) {
        window.history.replaceState(window.history.state, "", conversationPath(conversation.id));
      }
      setGenerating(false);
    } catch (caught) {
      if (isAbortError(caught)) {
        if (activeRetryRef.current) setRetryRequest(activeRetryRef.current);
        setError(t("chat.cancelled"));
      } else {
        if (activeConversationId) setRetryRequest({ conversationId: activeConversationId, content: text, requestId: id, attachmentIds });
        setError(caught instanceof ApiError && caught.code === "CONVERSATION_ARCHIVED" ? t("chat.archivedError") : t("chat.generationError"));
      }
      setGenerating(false);
    } finally {
      streamAbortRef.current = null;
      activeRetryRef.current = null;
      releaseSubmission(sendingRef);
    }
  }

  async function regenerate(message: ChatMessage) {
    if (!selected || !canRegenerate(message) || !claimSubmission(sendingRef)) return;
    const controller = new AbortController();
    streamAbortRef.current = controller;
    setError("");
    setRetryRequest(null);
    setGenerating(true);
    try {
      let streamState = createChatStreamState(messages);
      await api.regenerateMessage(selected.id, message.id, streamEvent => {
        streamState = applyStreamEvent(streamEvent, streamState);
        updateConversationFromStream(streamEvent);
        if (streamEvent.type === "message.failed") setError(t("chat.regenerateError"));
      }, createChatRequestId(), controller.signal);
    } catch (caught) {
      if (isAbortError(caught)) {
        setError(t("chat.cancelled"));
      } else {
        setError(t("chat.regenerateError"));
      }
    } finally {
      setGenerating(false);
      streamAbortRef.current = null;
      releaseSubmission(sendingRef);
    }
  }

  function stopGeneration() {
    if (!streamAbortRef.current) return;
    streamAbortRef.current.abort();
    if (activeRetryRef.current) setRetryRequest(activeRetryRef.current);
    setMessages(current => stopChatStream(createChatStreamState(current)).messages);
    setGenerating(false);
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

  async function deleteSelected() {
    if (!selected) return;
    try {
      await api.deleteConversation(selected.id);
      setConversations(current => current.filter(item => item.id !== selected.id));
      setDeleteConfirmOpen(false);
      startNewChat();
    } catch (caught) {
      setDeleteConfirmOpen(false);
      setError(caught instanceof Error ? caught.message : t("chat.deleteError"));
    }
  }

  async function copyResponse(message: ChatMessage) {
    try {
      await navigator.clipboard.writeText(message.content);
      setCopiedMessageId(message.id);
      window.setTimeout(() => setCopiedMessageId(current => current === message.id ? null : current), 1800);
    } catch {
      setError(t("chat.copyError"));
    }
  }

  function openCreator(creator: Creator) {
    router.push(studioTransitionPath(creator, selected?.projectId ?? selectedProjectId));
  }

  return <ProtectedPage><div className="chat-page">
    <aside className="chat-sidebar">
      <div className="chat-sidebar-header"><div><p className="section-eyebrow">{t("chat.eyebrow")}</p><h1>{t("chat.title")}</h1></div><button type="button" className="chat-icon-button" onClick={startNewChat} aria-label={t("chat.newChat")}><Plus size={18} /></button></div>
      <button type="button" className="chat-new-button" onClick={startNewChat} disabled={generating}><Plus size={15} />{t("chat.newChat")}</button>
      <label className="chat-search"><Search size={15} /><input value={search} onChange={event => setSearch(event.target.value)} placeholder={t("chat.searchPlaceholder")} aria-label={t("chat.searchPlaceholder")} /></label>
      <div className="chat-history" aria-label={t("chat.history")}>
        {loading ? <div className="chat-sidebar-loading"><span className="loading-spinner" /></div> : groups.length === 0 ? <p className="chat-sidebar-empty">{t("chat.noConversations")}</p> : groups.map(group => <section key={group.label} className="chat-history-group"><span className="chat-history-label">{group.label}</span>{group.items.map(item => <button type="button" key={item.id} className={`chat-history-item ${selected?.id === item.id ? "is-active" : ""}`} onClick={() => selectConversation(item)} disabled={generating}><MessageCircle size={14} /><span>{item.title}</span>{item.projectId && <i aria-label={t("chat.projectContextActive")} />}</button>)}</section>)}
      </div>
      <div className="chat-sidebar-footer"><Sparkles size={15} /><span>{t("chat.providerNotice")}</span></div>
    </aside>
    <main className="chat-main">
      <header className="chat-main-header"><div className="chat-title-block"><span className="chat-header-icon"><MessageCircle size={18} /></span><div><p className="section-eyebrow">{t("chat.headerEyebrow")}</p><h2>{selected?.title ?? t("chat.newChat")}</h2></div></div>{selected && <div className="chat-header-actions">{renaming ? <div className="chat-rename"><input value={renameValue} onChange={event => setRenameValue(event.target.value)} onKeyDown={event => { if (event.key === "Enter") void saveRename(); if (event.key === "Escape") setRenaming(false); }} autoFocus /><button type="button" onClick={() => void saveRename()} aria-label={t("chat.saveRename")}><Check size={15} /></button><button type="button" onClick={() => setRenaming(false)} aria-label={t("common.cancel")}><X size={15} /></button></div> : <><button type="button" className="chat-header-action" onClick={() => { setRenameValue(selected.title); setRenaming(true); }}><Edit3 size={14} />{t("chat.rename")}</button><button type="button" className="chat-header-action" onClick={() => void archiveSelected()} disabled={generating}><Archive size={14} />{t("chat.archive")}</button><button type="button" className="chat-header-action is-danger" onClick={() => setDeleteConfirmOpen(true)} disabled={generating}><Trash2 size={14} />{t("chat.delete")}</button></>}</div>}</header>
      <div className="chat-context-bar" aria-label={t("chat.contextStatus")}>
        <div className="chat-context-item"><small>{t("chat.personalMemory")}</small><strong>{t("chat.personalMemoryDescription")}</strong></div>
        <div className="chat-context-item"><small>{t("chat.conversationHistory")}</small><strong>{t("chat.conversationHistoryDescription")}</strong></div>
        <div className={`chat-context-item chat-project-context ${selectedProject ? "is-active" : ""}`}><small>{t("chat.projectContext")}</small><strong>{selectedProject ? `${t("chat.projectContextActive")}: ${selectedProject.name}` : t("chat.noProjectContext")}</strong></div>
      </div>
      {!selected && <div className="chat-project-selector"><label htmlFor="chat-project">{t("chat.projectSelector")}</label><select id="chat-project" value={selectedProjectId} onChange={(event) => setSelectedProjectId(event.target.value)} disabled={loading}><option value="">{t("chat.noProject")}</option>{projects.map((project) => <option key={project.id} value={project.id}>{project.name}</option>)}</select><small>{t("chat.projectSelectorHint")}</small></div>}
      <div className="chat-messages" aria-live="polite">
        {loading ? <div className="chat-empty"><span className="loading-spinner" /></div> : error && !selected ? <div className="chat-empty"><CircleMessage /><h3>{error}</h3><button type="button" className="secondary-button" onClick={startNewChat}>{t("chat.newChat")}</button></div> : messages.length === 0 ? <div className="chat-empty"><span className="chat-empty-icon"><Sparkles size={22} /></span><h3>{t("chat.emptyTitle")}</h3><p>{t("chat.emptyDescription")}</p></div> : <>{messages.map(message => <article className={`chat-message chat-message-${message.role.toLowerCase()} ${message.status === "Failed" ? "is-failed" : ""}`} key={message.id}><div className="chat-message-avatar">{message.role.toLowerCase() === "user" ? "T" : <Sparkles size={15} />}</div><div className="chat-message-copy"><span className="chat-message-role">{message.role.toLowerCase() === "user" ? t("chat.you") : t("chat.taslim")}</span><ChatMessageContent role={message.role} status={message.status} content={message.content} />{message.role === "Assistant" && message.content && <div className="chat-message-tools"><button type="button" onClick={() => void copyResponse(message)} aria-label={t("chat.copyResponse")}><Copy size={13} />{copiedMessageId === message.id ? t("chat.copied") : t("chat.copyResponse")}</button>{canRegenerate(message) && <button type="button" onClick={() => void regenerate(message)}><RefreshCw size={13} />{t("chat.regenerate")}</button>}</div>}{message.status === "Failed" && retryRequest && <button type="button" className="chat-retry-button" onClick={() => void send(undefined, retryRequest)} disabled={generating}><RefreshCw size={13} />{t("chat.retry")}</button>}{message.isTestResponse && <small className="chat-test-badge">{t("chat.testResponse")}</small>}</div></article>)}<div ref={messagesEndRef} /></>}
      </div>
      {error && selected && <div className="chat-inline-error" role="alert"><span>{error}</span><div>{retryRequest && <button type="button" className="chat-inline-retry" onClick={() => void send(undefined, retryRequest)} disabled={generating}>{t("chat.retry")}</button>}<button type="button" className="chat-inline-dismiss" onClick={() => { setError(""); setRetryRequest(null); }} aria-label={t("chat.dismissError")}><X size={14} /></button></div></div>}
      <div className="chat-creator-handoff"><span>{t("chat.handoffHint")}</span><div>{creatorActions.map((creator) => { const Icon = creator.icon; return <button type="button" key={creator.id} onClick={() => openCreator(creator.id)}><Icon size={13} />{t(creator.label)}</button>; })}</div></div>
      <form className="chat-composer" onSubmit={send}>
        {attachments.length > 0 && <div className="chat-attachment-list" aria-label={t("chat.attachments")}><div className="chat-attachment-heading"><span>{t("chat.attachments")}</span><button type="button" onClick={() => void clearAttachments()} disabled={generating}>{t("chat.clearAttachments")}</button></div>{attachments.map(file => <span className="chat-attachment-chip" key={file.id}><Paperclip size={12} /><span><strong>{file.originalFileName}</strong><small>{formatFileSize(file.sizeBytes)}</small></span><button type="button" onClick={() => void removeAttachment(file)} aria-label={`${t("chat.removeAttachment")}: ${file.originalFileName}`} disabled={generating}><X size={12} /></button></span>)}</div>}
        {uploadProgress && <div className="chat-upload-progress" role="status"><span>{t("chat.uploadProgress", { complete: String(uploadProgress.complete), total: String(uploadProgress.total), file: uploadProgress.fileName })}</span><i><b style={{ width: `${Math.max(8, (uploadProgress.complete / uploadProgress.total) * 100)}%` }} /></i></div>}
        <textarea value={content} onChange={event => { setContent(event.target.value); if (error) { setError(""); setRetryRequest(null); } }} onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey) void send(event); }} placeholder={t("chat.composerPlaceholder")} maxLength={20000} disabled={generating || !!uploadProgress} aria-label={t("chat.composerPlaceholder")} />
        <div className="chat-composer-footer"><span>{uploadProgress ? t("chat.fileUploading") : t("chat.composerHint")}</span><div className="chat-composer-actions"><input ref={fileInputRef} type="file" className="visually-hidden" multiple accept=".pdf,.docx,.txt,.md,.csv,.xlsx,.jpg,.jpeg,.png,.webp" onChange={event => void uploadSelectedFiles(event.target.files)} /><button type="button" className="chat-attach-button" onClick={() => fileInputRef.current?.click()} disabled={generating || !!uploadProgress} aria-label={t("chat.attachFile")}><Paperclip size={16} /></button>{generating ? <button type="button" className="chat-stop-button" onClick={stopGeneration} aria-label={t("chat.stopGeneration")}><Square size={13} />{t("chat.stop")}</button> : <button type="submit" className="chat-send-button" disabled={!!uploadProgress || !content.trim()} aria-label={t("chat.send")}><Send size={16} /></button>}</div></div>
      </form>
    </main>
    {deleteConfirmOpen && <div className="modal-backdrop" role="presentation"><section className="modal-card chat-delete-dialog" role="dialog" aria-modal="true" aria-labelledby="chat-delete-title"><div className="modal-heading"><div><p className="section-eyebrow">{t("chat.delete")}</p><h2 id="chat-delete-title">{t("chat.deleteConfirmTitle")}</h2></div><button type="button" className="modal-close" onClick={() => setDeleteConfirmOpen(false)} aria-label={t("common.close")}><X size={16} /></button></div><p>{t("chat.deleteConfirmDescription")}</p><div className="modal-actions"><button type="button" className="secondary-button" onClick={() => setDeleteConfirmOpen(false)}>{t("common.cancel")}</button><button type="button" className="chat-delete-confirm-button" onClick={() => void deleteSelected()}>{t("chat.delete")}</button></div></section></div>}
  </div></ProtectedPage>;
}

function CircleMessage() { return <span className="chat-empty-icon"><MoreHorizontal size={22} /></span>; }
