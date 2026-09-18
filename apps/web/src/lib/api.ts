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

const API_URL = (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000").replace(/\/$/, "");
let csrfToken: string | null = null;

async function csrf() {
  const response = await fetch(`${API_URL}/api/auth/csrf`, { credentials: "include" });
  if (!response.ok) throw new Error("CSRF token unavailable");
  const body = await response.json() as { token: string };
  csrfToken = body.token;
  return csrfToken;
}

async function request<T>(path: string, init: RequestInit = {}, withCsrf = false): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set("Content-Type", "application/json");
  if (withCsrf) headers.set("X-CSRF-TOKEN", csrfToken ?? await csrf());
  const response = await fetch(`${API_URL}${path}`, { ...init, headers, credentials: "include" });
  if (response.status === 204) return undefined as T;
  const body = await response.json().catch(() => null) as T & { error?: { message?: string } } | null;
  if (!response.ok) {
    if (response.status === 400 && withCsrf && !csrfToken) {
      csrfToken = null;
    }
    throw new ApiError(response.status, body?.error?.message ?? "Something went wrong.");
  }
  return body as T;
}

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

export const api = {
  me: () => request<AuthResponse>("/api/auth/me"),
  register: async (input: RegisterInput) => { await csrf(); return request<AuthResponse>("/api/auth/register", { method: "POST", body: JSON.stringify(input) }, true); },
  login: async (input: LoginInput) => { await csrf(); return request<AuthResponse>("/api/auth/login", { method: "POST", body: JSON.stringify(input) }, true); },
  logout: () => request<{ success: boolean }>("/api/auth/logout", { method: "POST" }, true),
  updateProfile: (input: ProfileInput) => request<AuthResponse>("/api/auth/profile", { method: "PATCH", body: JSON.stringify(input) }, true),
  listProjects: (workspaceId: string, status: "Active" | "Archived") => request<Project[]>(`/api/workspaces/${workspaceId}/projects?status=${status}`),
  getWorkspace: (workspaceId: string) => request<Workspace>(`/api/workspaces/${workspaceId}`),
  createProject: (workspaceId: string, input: ProjectInput) => request<Project>(`/api/workspaces/${workspaceId}/projects`, { method: "POST", body: JSON.stringify(input) }, true),
  getProject: (projectId: string) => request<Project>(`/api/projects/${projectId}`),
  updateProject: (projectId: string, input: ProjectInput) => request<Project>(`/api/projects/${projectId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  archiveProject: (projectId: string) => request<Project>(`/api/projects/${projectId}/archive`, { method: "POST" }, true),
  restoreProject: (projectId: string) => request<Project>(`/api/projects/${projectId}/restore`, { method: "POST" }, true),
};
