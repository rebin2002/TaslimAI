import { createSseParser, type TaslimSseEvent } from "@/lib/sse";
import { logChatDiagnostic, safeStreamError } from "@/lib/diagnostics";

export type User = {
  id: string;
  email: string;
  displayName: string;
  preferredLanguage: "en" | "ar" | "ku";
  personalWorkspaceId: string;
  createdAt: string;
};

export type Workspace = {
  id: string;
  name: string;
  slug: string;
  type: string;
  role: string;
};

export type AuthResponse = { user: User; personalWorkspace: Workspace };
export type PasswordPolicy = {
  requiredLength: number;
  requireUppercase: boolean;
  requireLowercase: boolean;
  requireDigit: boolean;
  requireNonAlphanumeric: boolean;
  requiredUniqueChars: number;
};

export type Project = {
  id: string;
  workspaceId: string;
  name: string;
  description: string | null;
  type: string;
  status: "Active" | "Archived";
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
};

export type RegisterInput = { displayName: string; email: string; password: string; preferredLanguage?: string };
export type LoginInput = { email: string; password: string };
export type ProfileInput = { displayName: string; preferredLanguage: string };
export type ProjectInput = { name: string; description?: string; type?: string };
export type Conversation = {
  id: string;
  workspaceId: string;
  projectId: string | null;
  title: string;
  status: "Active" | "Archived";
  createdAt: string;
  updatedAt: string;
  lastMessageAt: string | null;
};
export type ChatMessage = {
  id: string;
  conversationId: string;
  role: "User" | "Assistant";
  content: string;
  status: "Pending" | "Completed" | "Failed";
  createdAt: string;
  sequence: number;
  isTestResponse?: boolean;
};
export type SendMessageResponse = { conversation: Conversation; userMessage: ChatMessage; assistantMessage: ChatMessage };
export type ChatStreamData = {
  conversation?: Conversation;
  userMessage?: ChatMessage;
  assistantMessage?: ChatMessage;
  messageId?: string;
  delta?: string;
  code?: string;
  message?: string;
};
export type ChatStreamEvent = TaslimSseEvent<ChatStreamData>;

const API_URL = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");
let csrfToken: string | null = null;

function requestId() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

async function csrf(forceRefresh = false) {
  if (csrfToken && !forceRefresh) return csrfToken;
  const response = await fetch(`${API_URL}/api/auth/csrf`, { credentials: "include" });
  if (!response.ok) throw new Error("CSRF token unavailable");
  const body = await response.json() as { token: string };
  csrfToken = body.token;
  return csrfToken;
}

type ErrorBody = { error?: { code?: string; message?: string; fields?: Record<string, string[]> } };

async function parseError(response: Response) {
  return await response.json().catch(() => null) as ErrorBody | null;
}

async function request<T>(path: string, init: RequestInit = {}, withCsrf = false, retryCsrf = true): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set("Content-Type", "application/json");
  if (withCsrf) headers.set("X-CSRF-TOKEN", csrfToken ?? await csrf());
  const response = await fetch(`${API_URL}${path}`, { ...init, headers, credentials: "include" });
  if (response.status === 204) return undefined as T;
  const body = await response.json().catch(() => null) as T & ErrorBody | null;
  if (!response.ok) {
    if (response.status === 400 && withCsrf && retryCsrf && body?.error?.code === "CSRF_VALIDATION_FAILED") {
      csrfToken = null;
      await csrf(true);
      return request<T>(path, init, true, false);
    }
    throw new ApiError(response.status, body?.error?.message ?? "Something went wrong.", body?.error?.fields, body?.error?.code);
  }
  return body as T;
}

async function streamRequest(path: string, payload: unknown, onEvent: (event: ChatStreamEvent) => void, retryCsrf = true): Promise<void> {
  logChatDiagnostic("STREAM_REQUEST_STARTED");
  try {
    const headers = new Headers({ "Content-Type": "application/json", Accept: "text/event-stream" });
    headers.set("X-CSRF-TOKEN", csrfToken ?? await csrf());
    const response = await fetch(`${API_URL}${path}`, { method: "POST", headers, credentials: "include", body: JSON.stringify(payload) });
    if (!response.ok) {
      const body = await parseError(response);
      if (response.status === 400 && retryCsrf && body?.error?.code === "CSRF_VALIDATION_FAILED") {
        csrfToken = null;
        await csrf(true);
        return streamRequest(path, payload, onEvent, false);
      }
      throw new ApiError(response.status, body?.error?.message ?? "Something went wrong.", body?.error?.fields, body?.error?.code);
    }
    if (!response.body) throw new ApiError(502, "Streaming is unavailable.", undefined, "STREAM_UNAVAILABLE");

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    const parser = createSseParser<ChatStreamData>(event => {
      logChatDiagnostic(`SSE_EVENT_RECEIVED ${event.type}`);
      onEvent(event);
    });
    try {
      while (true) {
        const result = await reader.read();
        parser.push(decoder.decode(result.value ?? new Uint8Array(), { stream: !result.done }));
        if (result.done) break;
      }
      parser.push(decoder.decode());
      parser.end();
      logChatDiagnostic("STREAM_FINISHED");
    } finally {
      reader.releaseLock();
    }
  } catch (error) {
    logChatDiagnostic(`STREAM_ERROR ${safeStreamError(error)}`);
    throw error;
  }
}

export class ApiError extends Error {
  constructor(public status: number, message: string, public fields?: Record<string, string[]>, public code?: string) { super(message); }
}

export const api = {
  me: () => request<AuthResponse>("/api/auth/me"),
  passwordPolicy: () => request<PasswordPolicy>("/api/auth/password-policy"),
  register: async (input: RegisterInput) => { const result = await request<AuthResponse>("/api/auth/register", { method: "POST", body: JSON.stringify(input) }, true); csrfToken = null; await csrf(true); return result; },
  login: async (input: LoginInput) => { const result = await request<AuthResponse>("/api/auth/login", { method: "POST", body: JSON.stringify(input) }, true); csrfToken = null; await csrf(true); return result; },
  logout: async () => { const result = await request<{ success: boolean }>("/api/auth/logout", { method: "POST" }, true); csrfToken = null; await csrf(true); return result; },
  updateProfile: (input: ProfileInput) => request<AuthResponse>("/api/auth/profile", { method: "PATCH", body: JSON.stringify(input) }, true),
  listProjects: (workspaceId: string, status: "Active" | "Archived") => request<Project[]>(`/api/workspaces/${workspaceId}/projects?status=${status}`),
  getWorkspace: (workspaceId: string) => request<Workspace>(`/api/workspaces/${workspaceId}`),
  createProject: (workspaceId: string, input: ProjectInput) => request<Project>(`/api/workspaces/${workspaceId}/projects`, { method: "POST", body: JSON.stringify(input) }, true),
  getProject: (projectId: string) => request<Project>(`/api/projects/${projectId}`),
  updateProject: (projectId: string, input: ProjectInput) => request<Project>(`/api/projects/${projectId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  archiveProject: (projectId: string) => request<Project>(`/api/projects/${projectId}/archive`, { method: "POST" }, true),
  restoreProject: (projectId: string) => request<Project>(`/api/projects/${projectId}/restore`, { method: "POST" }, true),
  listConversations: (workspaceId: string, status: "Active" | "Archived" = "Active") => request<Conversation[]>(`/api/workspaces/${workspaceId}/conversations?status=${status}`),
  createConversation: (workspaceId: string, input: { title?: string; projectId?: string } = {}) => request<Conversation>(`/api/workspaces/${workspaceId}/conversations`, { method: "POST", body: JSON.stringify(input) }, true),
  getConversation: (conversationId: string) => request<Conversation>(`/api/conversations/${conversationId}`),
  getMessages: (conversationId: string) => request<ChatMessage[]>(`/api/conversations/${conversationId}/messages`),
  renameConversation: (conversationId: string, title: string) => request<Conversation>(`/api/conversations/${conversationId}`, { method: "PATCH", body: JSON.stringify({ title }) }, true),
  archiveConversation: (conversationId: string) => request<Conversation>(`/api/conversations/${conversationId}/archive`, { method: "POST" }, true),
  sendMessage: (conversationId: string, content: string, id = requestId()) => request<SendMessageResponse>(`/api/conversations/${conversationId}/messages`, { method: "POST", body: JSON.stringify({ content, requestId: id }) }, true),
  streamMessage: (conversationId: string, content: string, onEvent: (event: ChatStreamEvent) => void, id = requestId()) => streamRequest(`/api/conversations/${conversationId}/messages/stream`, { content, requestId: id }, onEvent),
};
