import { createSseParser, type TaslimSseEvent } from "./sse";
import { API_URL, assetFileUrl, assetRepresentationUrl } from "./apiBase";

export type User = {
  id: string;
  email: string;
  displayName: string;
  preferredLanguage: "en" | "ar" | "ku";
  personalWorkspaceId: string;
  createdAt: string;
  defaultGenerationLanguage: "en" | "ar" | "ku";
  timeZone: string;
  outputPreference: "concise" | "balanced" | "detailed";
  includeSourceLinks: boolean;
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
  instructions: string | null;
  contextNotes: string | null;
  type: string;
  status: "Active" | "Archived";
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
};

export type ProjectOverview = {
  project: Project;
  workspace: Workspace;
  counts: { files: number; assets: number; conversations: number; activity: number };
  conversations: Conversation[];
  recentActivity: ActivityItem[];
};

export type RegisterInput = { displayName: string; email: string; password: string; preferredLanguage?: string };
export type LoginInput = { email: string; password: string };
export type ProfileInput = {
  displayName: string;
  preferredLanguage: string;
  defaultGenerationLanguage?: string;
  timeZone?: string;
  outputPreference?: "concise" | "balanced" | "detailed";
  includeSourceLinks?: boolean;
};
export type ChangePasswordInput = { currentPassword: string; newPassword: string };
export type ProjectInput = { name: string; description?: string; instructions?: string; contextNotes?: string; type?: string };
export type PersonalMemory = {
  id: string;
  workspaceId: string;
  category: string;
  title: string;
  content: string;
  source: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
};
export type PersonalMemoryInput = { category: string; title: string; content: string };
export type StoredFile = {
  id: string;
  workspaceId: string;
  projectId: string | null;
  conversationId: string | null;
  originalFileName: string;
  contentType: string;
  extension: string;
  sizeBytes: number;
  storageProvider: string;
  status: "Uploading" | "Ready" | "Processing" | "Failed" | "Deleted";
  textExtractionStatus: "NotStarted" | "Processing" | "Ready" | "Failed" | "NotApplicable";
  extractedTextLength: number | null;
  createdAt: string;
  processedAt: string | null;
};
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
export type UsageSummary = {
  totalRequests: number;
  completedRequests: number;
  failedRequests: number;
  cancelledRequests: number;
  refundedRequests: number;
  inputTokens: number;
  cachedInputTokens: number;
  outputTokens: number;
  customerChargedAmount: number;
  chargedUnit: string;
};
export type UsageTransaction = {
  id: string;
  feature: string;
  status: "Pending" | "Completed" | "Failed" | "Cancelled" | "Refunded";
  inputTokens: number | null;
  cachedInputTokens: number | null;
  outputTokens: number | null;
  chargedAmount: number;
  chargedUnit: string;
  createdAt: string;
  completedAt: string | null;
  failureCode: string | null;
};
export type UsageHistory = { items: UsageTransaction[]; page: number; pageSize: number; totalCount: number; totalPages: number };
export type BillingPlan = { code: string; name: string; monthlyPriceUsd: number; monthlyCreditAllowance: number; currency: string };
export type BillingPlanOption = BillingPlan & { isCurrent: boolean };
export type BillingSubscription = { status: string; currentPeriodStart: string; currentPeriodEnd: string; nextRenewalAt: string; cancelAtPeriodEnd: boolean };
export type BillingPeriod = { id: string; status: string; startsAt: string; endsAt: string; includedCredits: number };
export type BillingCredits = { includedGranted: number; includedRemaining: number; purchasedRemaining: number; adjustmentBalance: number; totalRemaining: number };
export type CreditLedgerEntry = { id: string; type: string; amount: number; reason: string; createdAt: string };
export type BillingPaymentStatus = { status: string; provider: string | null; lastFailureReason: string | null; lastPaymentAt: string | null };
export type BillingActions = { checkoutAvailable: boolean; upgradeAvailable: boolean; downgradeAvailable: boolean; cancelAvailable: boolean; disabledReason: string };
export type BillingAccount = { currentPlan: BillingPlan; subscription: BillingSubscription; billingPeriod: BillingPeriod; credits: BillingCredits; transactions: CreditLedgerEntry[]; availablePlans: BillingPlanOption[]; paymentStatus: BillingPaymentStatus; actions: BillingActions };
export type AdminUsageSummary = {
  fromUtc: string;
  toUtc: string;
  transactionCount: number;
  successfulCount: number;
  failedCount: number;
  cancelledCount: number;
  refundedCount: number;
  totalProviderCostUsd: number;
  totalCustomerChargesUsd: number;
  pendingEstimatedProviderCostUsd: number;
  anomalousCount: number;
  currency: string;
};
export type AdminUsageFeatureBreakdown = { feature: string; transactionCount: number; successfulCount: number; failedCount: number; cancelledCount: number; providerCostUsd: number; customerChargesUsd: number };
export type AdminUsageDailyBreakdown = { dayUtc: string; transactionCount: number; providerCostUsd: number; customerChargesUsd: number };
export type AdminUsageWorkspaceBreakdown = { workspaceId: string; workspaceName: string; transactionCount: number; providerCostUsd: number; customerChargesUsd: number };
export type AdminUsageUserBreakdown = { userId: string; email: string | null; displayName: string; transactionCount: number; providerCostUsd: number; customerChargesUsd: number };
export type AdminUsageStatusBreakdown = { status: string; transactionCount: number; successfulCount: number; failedCount: number; cancelledCount: number; providerCostUsd: number; customerChargesUsd: number };
export type AdminUsageBreakdowns = { byFeature: AdminUsageFeatureBreakdown[]; byDay: AdminUsageDailyBreakdown[]; byWorkspace: AdminUsageWorkspaceBreakdown[]; byUser: AdminUsageUserBreakdown[]; byStatus: AdminUsageStatusBreakdown[] };
export type AdminUsageTransaction = {
  id: string; createdAt: string; completedAt: string | null; workspaceId: string; workspaceName: string; userId: string; userEmail: string | null; userDisplayName: string; projectId: string | null; conversationId: string | null; generationJobId: string | null; feature: string; status: string; provider: string; model: string; inputTokens: number | null; cachedInputTokens: number | null; outputTokens: number | null; imageInputTokens: number | null; imageOutputTokens: number | null; latencyMs: number | null; estimatedProviderCostUsd: number | null; providerCostUsd: number; chargedAmount: number; currency: string; costBasis: string | null; pricingVersion: string | null; pricingSnapshotJson: string | null; safeMetadataJson: string | null; failureCode: string | null; isAnomalous: boolean; anomalyCode: string | null; refundedAt: string | null;
};
export type AdminUsageTransactionList = { items: AdminUsageTransaction[]; page: number; pageSize: number; totalCount: number; totalPages: number };
export type AdminUsageReport = { summary: AdminUsageSummary; breakdowns: AdminUsageBreakdowns; transactions: AdminUsageTransactionList };
export type GenerationJobStatus = "Pending" | "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled";
export type GenerationJobOutput = { id: string; outputType: string; storedFileId: string | null; metadataJson: string | null; createdAt: string };
export type GenerationJob = {
  id: string;
  workspaceId: string;
  projectId: string | null;
  jobType: string;
  status: GenerationJobStatus;
  title: string | null;
  progressPercent: number;
  resultJson: string | null;
  errorCode: string | null;
  errorMessage: string | null;
  cancellationRequested: boolean;
  createdAt: string;
  queuedAt: string | null;
  startedAt: string | null;
  completedAt: string | null;
  failedAt: string | null;
  cancelledAt: string | null;
  outputs: GenerationJobOutput[];
};
export type GenerationJobList = { items: GenerationJob[]; page: number; pageSize: number; totalCount: number; totalPages: number };
export type MovieGuide = { id: string; visualLanguage: string; cameraLanguage: string; colorAndLighting: string; soundAndNarration: string; continuityRules: string; updatedAt: string };
export type MovieScene = { id: string; sequence: number; title: string; summary: string; durationSeconds: number | null; continuityNotes: string | null; narration: string | null; dialogue: string | null; shots: MovieShot[]; clips: MovieClip[] };
export type MovieShot = { id: string; sequence: number; description: string; cameraAndFraming: string | null; cameraMotion: string | null; durationSeconds: number | null; narration: string | null; dialogue: string | null; visualContinuityNotes: string | null; clips: MovieClip[] };
export type MovieCharacter = { id: string; name: string; description: string; appearance: string | null; voiceAndPerformance: string | null; continuityNotes: string | null; referenceAssetId: string | null };
export type MovieLocation = { id: string; name: string; description: string; visualContinuityNotes: string | null; referenceAssetId: string | null };
export type MovieClip = { id: string; movieSceneId: string | null; movieShotId: string | null; generationJobId: string | null; assetId: string | null; status: string; durationSeconds: number | null; metadataJson: string | null; continuitySnapshotJson: string | null };
export type MovieAssembly = { id: string; generationJobId: string | null; assetId: string | null; status: string; outputFormat: string; metadataJson: string | null; createdAt: string; completedAt: string | null };
export type MovieProject = { id: string; workspaceId: string; projectId: string | null; mode: "Quick" | "Full"; status: string; title: string; description: string; durationSeconds: number; aspectRatio: string; style: string; language: string; additionalInstructions: string | null; createdAt: string; updatedAt: string; guide: MovieGuide; scenes: MovieScene[]; characters: MovieCharacter[]; locations: MovieLocation[]; clips: MovieClip[]; assemblies: MovieAssembly[] };
export type MovieProviderReadiness = { ready: boolean; supportedOperations: string[] };
export type MovieStudioResponse = { project: MovieProject; job: GenerationJob | null };
export type MovieStudioCreateInput = { workspaceId: string; projectId?: string | null; mode: "Quick" | "Full"; title: string; description: string; durationSeconds: number; aspectRatio: string; style: string; language: string; additionalInstructions?: string | null; visualLanguage?: string | null; cameraLanguage?: string | null; colorAndLighting?: string | null; soundAndNarration?: string | null; continuityRules?: string | null };
export type ActivityItem = {
  jobId: string;
  workspaceId: string;
  projectId: string | null;
  jobType: "image" | "document" | "presentation" | "research" | "social" | "voice" | "music" | "movie" | "other";
  title: string;
  status: "Queued" | "Running" | "Completed" | "Failed" | "Cancelled";
  progressPercent: number;
  createdAt: string;
  completedAt: string | null;
  isRead: boolean;
  safeFailureMessage: string | null;
  assetId: string | null;
};
export type ActivityList = { items: ActivityItem[]; page: number; pageSize: number; totalCount: number; totalPages: number; unreadCount: number };
export type GlobalSearchResultType = "projects" | "conversations" | "assets" | "files" | "generation";
export type GlobalSearchResult = {
  type: GlobalSearchResultType;
  id: string;
  title: string;
  description: string | null;
  projectId: string | null;
  conversationId: string | null;
  assetId: string | null;
  projectName: string | null;
  status: string | null;
  metadata: string | null;
  createdAt: string;
  updatedAt: string | null;
};
export type GlobalSearchGroup = { type: GlobalSearchResultType; count: number; items: GlobalSearchResult[] };
export type GlobalSearchResponse = { query: string; totalCount: number; groups: GlobalSearchGroup[] };
export type ImageGenerationInput = {
  workspaceId: string;
  projectId?: string | null;
  description: string;
  style: string;
  aspectRatio: string;
  quality: string;
  title?: string | null;
  mood?: string | null;
  background?: string | null;
  textInImage?: string | null;
};
export type ImageJobResult = { assetId?: string; assetType?: "image"; format?: string; width?: number | null; height?: number | null; aspectRatio?: string; quality?: string };
export type VoiceGenerationInput = {
  workspaceId: string;
  projectId?: string | null;
  text: string;
  language: "en" | "ar" | "ku";
  voiceStyle: string;
  speakingStyle: string;
  instructions?: string | null;
  title?: string | null;
};
export type VoiceJobResult = {
  assetId?: string;
  assetType?: "audio";
  contentType?: string;
  format?: string;
  language?: string;
  voiceStyle?: string;
  speakingStyle?: string;
  sizeBytes?: number;
  durationMilliseconds?: number | null;
  sampleRateHz?: number | null;
};
export type DocumentGenerationInput = {
  workspaceId: string;
  projectId?: string | null;
  title?: string | null;
  description: string;
  documentType?: "auto" | "report" | "proposal" | "business_letter" | "company_profile" | "meeting_minutes" | "article" | "general";
  length?: "short" | "standard" | "detailed";
  audience?: string | null;
  additionalInstructions?: string | null;
  attachmentIds: string[];
  language?: "auto" | "en" | "ar" | "ku";
  outputFormat?: "docx" | "pdf" | "both";
  tone?: "professional" | "formal" | "friendly" | "persuasive" | "neutral" | "concise" | "academic";
  includeTableOfContents?: boolean;
};
export type DocumentJobResult = { assetId?: string; documentType?: "document"; title?: string; language?: string; summary?: string; sections?: { heading: string; blocks: { type: string; text?: string | null; items?: string[] | null; rows?: { cells: string[] }[] | null }[] }[]; representations?: { id: string; type: string; fileName: string; contentType: string }[] };
export type PresentationGenerationInput = {
  workspaceId: string;
  projectId?: string | null;
  title?: string | null;
  description: string;
  presentationType?: "auto" | "business" | "company_profile" | "sales" | "investor" | "proposal" | "training" | "project_update" | "report" | "educational" | "general";
  length?: "short" | "standard" | "detailed";
  tone?: "professional" | "formal" | "friendly" | "persuasive" | "neutral" | "concise" | "academic";
  language?: "auto" | "en" | "ar" | "ku";
  audience?: string | null;
  additionalInstructions?: string | null;
  brandCompany?: string | null;
  includeAgenda?: boolean;
  includeClosingNextSteps?: boolean;
  attachmentIds: string[];
};
export type PresentationJobResult = {
  assetId?: string;
  presentationType?: string;
  title?: string;
  subtitle?: string | null;
  language?: string;
  slideCount?: number;
  previewSlides?: { order: number; type: string; title: string; subtitle?: string | null; blocks?: { type: string; text?: string | null; items?: string[]; columns?: { heading: string; items: string[] }[]; rows?: { cells: string[] }[]; metrics?: { label: string; value: string; detail?: string | null }[]; label?: string | null; value?: string | null }[] }[];
  representations?: { id: string; type: string; fileName: string; contentType: string }[];
};
export type ResearchGenerationInput = {
  workspaceId: string;
  projectId?: string | null;
  question: string;
  title?: string | null;
  depth?: "quick" | "standard" | "deep";
  reportType?: "research_report" | "market_research" | "competitor_research" | "company_research" | "product_research" | "industry_research" | "general_research";
  language?: "auto" | "en" | "ar" | "ku";
  audience?: string | null;
  geographicFocus?: string | null;
  timePeriod?: string | null;
  additionalInstructions?: string | null;
  preferredDomains?: string | null;
  excludedDomains?: string | null;
  useWebSources: boolean;
  attachmentIds: string[];
};
export type MusicGenerationInput = {
  workspaceId: string;
  projectId?: string | null;
  description: string;
  purpose: string;
  genre: string;
  mood: string;
  durationSeconds: number;
  vocalPreference: string;
  language: string;
  title?: string | null;
  additionalInstructions?: string | null;
};
export type MusicJobResult = { assetId?: string; assetType?: "music"; title?: string; format?: string; durationSeconds?: number | null; vocalPreference?: string; language?: string };
export type ResearchReportBlock = { type: string; text?: string | null; items?: string[] | null; rows?: { cells: string[] }[] | null; citationIds?: string[] };
export type ResearchJobResult = {
  assetId?: string;
  researchType?: "research";
  title?: string;
  subtitle?: string | null;
  language?: string;
  executiveSummary?: string;
  keyFindings?: ResearchReportBlock[];
  sections?: { heading: string; blocks: ResearchReportBlock[] }[];
  conclusion?: string;
  sourceCount?: number;
  sources?: ResearchSource[];
  representations?: { id: string; type: string; fileName: string; contentType: string }[];
};
export type SocialGenerationInput = {
  workspaceId: string;
  projectId?: string | null;
  prompt: string;
  socialType?: "auto" | "announcement" | "product_launch" | "promotion" | "educational" | "thought_leadership" | "company_update" | "event" | "community" | "general";
  platform?: "instagram" | "facebook" | "linkedin" | "x" | "tiktok" | "multi";
  tone?: "professional" | "friendly" | "persuasive" | "educational" | "playful" | "concise" | "thoughtful";
  language?: "auto" | "en" | "ar" | "ku";
  audience?: string | null;
  brandVoice?: string | null;
  callToAction?: string | null;
  includeHashtags?: boolean;
  includeEmojis?: boolean;
  generateVariants?: boolean;
  assetIds: string[];
  attachmentIds: string[];
};
export type SocialPost = { order: number; hook: string; body: string; callToAction?: string | null; hashtags?: string[]; altText?: string | null; visualDirection?: string | null; assetRefs?: string[] };
export type SocialJobResult = { assetId?: string; socialType?: string; platform?: string; title?: string; language?: string; postCount?: number; posts?: SocialPost[] };
export type ResearchSource = {
  citationId: string;
  url: string | null;
  title: string;
  domain: string;
  publisher: string | null;
  publishedAt: string | null;
  retrievedAt: string;
  sourceType: string;
  snippet: string | null;
  searchQuery: string | null;
  rank: number;
  isSelected: boolean;
  evidence?: ResearchEvidence[];
};
export type ResearchEvidence = { topic: string; excerpt: string; context: string | null; publishedAt: string | null };
export type AssetStatus = "Active" | "Archived";
export type AssetType = "image" | "document" | "presentation" | "video" | "audio" | "music" | "research" | "social" | "file" | "other";
export type Asset = {
  id: string;
  workspaceId: string;
  projectId: string | null;
  projectName: string | null;
  name: string;
  description: string | null;
  assetType: AssetType;
  mimeType: string | null;
  status: AssetStatus;
  hasFile: boolean;
  canPreview: boolean;
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
  representations: AssetRepresentation[];
};
export type AssetRepresentation = { id: string; representationType: string; fileName: string; contentType: string; sizeBytes: number; createdAt: string };
export type AssetList = { items: Asset[]; page: number; pageSize: number; totalCount: number; totalPages: number };
export type AssetFilters = { projectId?: string; assetType?: AssetType; status?: AssetStatus; search?: string; page?: number; pageSize?: number };
export type AssetInput = { name: string; description?: string | null; projectId?: string | null };

let csrfToken: string | null = null;

function requestId() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

async function csrf(forceRefresh = false) {
  if (csrfToken && !forceRefresh) return csrfToken;
  const response = await fetch(`${API_URL}/api/auth/csrf`, { credentials: "include", cache: "no-store" });
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

async function requestForm<T>(path: string, form: FormData, withCsrf = false, retryCsrf = true): Promise<T> {
  const headers = new Headers();
  if (withCsrf) headers.set("X-CSRF-TOKEN", csrfToken ?? await csrf());
  const response = await fetch(`${API_URL}${path}`, { method: "POST", headers, credentials: "include", body: form });
  const body = await response.json().catch(() => null) as T & ErrorBody | null;
  if (!response.ok) {
    if (response.status === 400 && withCsrf && retryCsrf && body?.error?.code === "CSRF_VALIDATION_FAILED") {
      csrfToken = null;
      await csrf(true);
      return requestForm<T>(path, form, true, false);
    }
    throw new ApiError(response.status, body?.error?.message ?? "Something went wrong.", body?.error?.fields, body?.error?.code);
  }
  return body as T;
}

async function streamRequest(path: string, payload: unknown, onEvent: (event: ChatStreamEvent) => void, retryCsrf = true): Promise<void> {
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
  const parser = createSseParser<ChatStreamData>(onEvent);
  try {
    while (true) {
      const result = await reader.read();
      parser.push(decoder.decode(result.value ?? new Uint8Array(), { stream: !result.done }));
      if (result.done) break;
    }
    parser.push(decoder.decode());
    parser.end();
  } finally {
    reader.releaseLock();
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
  changePassword: (input: ChangePasswordInput) => request<{ success: boolean }>("/api/auth/password", { method: "POST", body: JSON.stringify(input) }, true),
  listProjects: (workspaceId: string, status: "Active" | "Archived") => request<Project[]>(`/api/workspaces/${workspaceId}/projects?status=${status}`),
  listWorkspaces: () => request<Workspace[]>('/api/workspaces'),
  getWorkspace: (workspaceId: string) => request<Workspace>(`/api/workspaces/${workspaceId}`),
  createProject: (workspaceId: string, input: ProjectInput) => request<Project>(`/api/workspaces/${workspaceId}/projects`, { method: "POST", body: JSON.stringify(input) }, true),
  getProject: (projectId: string) => request<Project>(`/api/projects/${projectId}`),
  getProjectOverview: (projectId: string) => request<ProjectOverview>(`/api/projects/${projectId}/overview`),
  updateProject: (projectId: string, input: ProjectInput) => request<Project>(`/api/projects/${projectId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  archiveProject: (projectId: string) => request<Project>(`/api/projects/${projectId}/archive`, { method: "POST" }, true),
  restoreProject: (projectId: string) => request<Project>(`/api/projects/${projectId}/restore`, { method: "POST" }, true),
  listConversations: (workspaceId: string, status: "Active" | "Archived" = "Active") => request<Conversation[]>(`/api/workspaces/${workspaceId}/conversations?status=${status}`),
  createConversation: (workspaceId: string, input: { title?: string; projectId?: string } = {}) => request<Conversation>(`/api/workspaces/${workspaceId}/conversations`, { method: "POST", body: JSON.stringify(input) }, true),
  getConversation: (conversationId: string) => request<Conversation>(`/api/conversations/${conversationId}`),
  getMessages: (conversationId: string) => request<ChatMessage[]>(`/api/conversations/${conversationId}/messages`),
  renameConversation: (conversationId: string, title: string) => request<Conversation>(`/api/conversations/${conversationId}`, { method: "PATCH", body: JSON.stringify({ title }) }, true),
  archiveConversation: (conversationId: string) => request<Conversation>(`/api/conversations/${conversationId}/archive`, { method: "POST" }, true),
  sendMessage: (conversationId: string, content: string, id = requestId(), attachmentIds: string[] = []) => request<SendMessageResponse>(`/api/conversations/${conversationId}/messages`, { method: "POST", body: JSON.stringify({ content, requestId: id, attachmentIds }) }, true),
  streamMessage: (conversationId: string, content: string, onEvent: (event: ChatStreamEvent) => void, id = requestId(), attachmentIds: string[] = []) => streamRequest(`/api/conversations/${conversationId}/messages/stream`, { content, requestId: id, attachmentIds }, onEvent),
  getUsageSummary: (workspaceId: string) => request<UsageSummary>(`/api/workspaces/${workspaceId}/usage/summary`),
  getUsageHistory: (workspaceId: string, page = 1, pageSize = 20) => request<UsageHistory>(`/api/workspaces/${workspaceId}/usage?page=${page}&pageSize=${pageSize}`),
  getBillingAccount: (workspaceId: string) => request<BillingAccount>(`/api/workspaces/${workspaceId}/billing`),
  getAdminUsageReport: (params: Record<string, string | number | undefined> = {}) => {
    const query = new URLSearchParams(Object.entries(params).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)]));
    return request<AdminUsageReport>(`/api/admin/usage/report${query.toString() ? `?${query.toString()}` : ""}`);
  },
  getAdminUsageTransaction: (id: string) => request<AdminUsageTransaction>(`/api/admin/usage/transactions/${id}`),
  createGenerationJob: (workspaceId: string, inputJson = "{}", title?: string) => request<GenerationJob>("/api/generation/jobs", { method: "POST", body: JSON.stringify({ workspaceId, jobType: "system.test", inputJson, title }) }, true),
  createImageGenerationJob: (input: ImageGenerationInput) => request<{ job: GenerationJob }>("/api/image-generation/jobs", { method: "POST", body: JSON.stringify(input) }, true).then((response) => response.job),
  createVoiceGenerationJob: (input: VoiceGenerationInput) => request<{ job: GenerationJob }>("/api/voice-generation/jobs", { method: "POST", body: JSON.stringify(input) }, true).then((response) => response.job),
  createDocumentGenerationJob: (input: DocumentGenerationInput) => request<{ job: GenerationJob }>("/api/document-generation/jobs", { method: "POST", body: JSON.stringify(input) }, true).then((response) => response.job),
  createPresentationGenerationJob: (input: PresentationGenerationInput) => request<{ job: GenerationJob }>("/api/presentation-generation/jobs", { method: "POST", body: JSON.stringify(input) }, true).then((response) => response.job),
  createResearchGenerationJob: (input: ResearchGenerationInput) => request<{ job: GenerationJob }>("/api/research-generation/jobs", { method: "POST", body: JSON.stringify(input) }, true).then((response) => response.job),
  createSocialGenerationJob: (input: SocialGenerationInput) => request<{ job: GenerationJob }>("/api/social-generation/jobs", { method: "POST", body: JSON.stringify(input) }, true).then((response) => response.job),
  getMovieProvider: () => request<{ provider: MovieProviderReadiness }>("/api/movie-studio/provider"),
  createMovieProject: (input: MovieStudioCreateInput) => request<MovieStudioResponse>("/api/movie-studio/projects", { method: "POST", body: JSON.stringify(input) }, true),
  getMovieProject: (id: string) => request<MovieProject>(`/api/movie-studio/projects/${id}`),
  updateMovieGuide: (id: string, input: Partial<MovieGuide>) => request<MovieProject>(`/api/movie-studio/projects/${id}/guide`, { method: "PATCH", body: JSON.stringify(input) }, true),
  addMovieScene: (id: string, input: { title: string; summary: string; durationSeconds?: number | null; continuityNotes?: string | null; narration?: string | null; dialogue?: string | null }) => request<MovieScene>(`/api/movie-studio/projects/${id}/scenes`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieCharacter: (id: string, input: { name: string; description: string; appearance?: string | null; voiceAndPerformance?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null }) => request<MovieCharacter>(`/api/movie-studio/projects/${id}/characters`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieLocation: (id: string, input: { name: string; description: string; visualContinuityNotes?: string | null; referenceAssetId?: string | null }) => request<MovieLocation>(`/api/movie-studio/projects/${id}/locations`, { method: "POST", body: JSON.stringify(input) }, true),
  generateMovieScene: (projectId: string, sceneId: string, input: { title?: string | null } = {}) => request<{ project: MovieProject; job: GenerationJob; clipId: string }>(`/api/movie-studio/projects/${projectId}/scenes/${sceneId}/generate`, { method: "POST", body: JSON.stringify(input) }, true),
  generateMovieShot: (shotId: string, input: { title?: string | null } = {}) => request<{ project: MovieProject; job: GenerationJob; clipId: string }>(`/api/movie-studio/shots/${shotId}/generate`, { method: "POST", body: JSON.stringify(input) }, true),
  createMusicGenerationJob: (input: MusicGenerationInput) => request<{ job: GenerationJob }>("/api/music-generation/jobs", { method: "POST", body: JSON.stringify(input) }, true).then((response) => response.job),
  getGenerationJob: (jobId: string) => request<GenerationJob>(`/api/generation/jobs/${jobId}`),
  getResearchSources: (jobId: string) => request<{ jobId: string; sources: ResearchSource[] }>(`/api/research-generation/jobs/${jobId}/sources`),
  listGenerationJobs: (workspaceId: string, page = 1, pageSize = 20) => request<GenerationJobList>(`/api/generation/jobs?workspaceId=${encodeURIComponent(workspaceId)}&page=${page}&pageSize=${pageSize}&jobType=system.test`),
  cancelGenerationJob: (jobId: string) => request<{ status: GenerationJobStatus; cancellationRequested?: boolean }>(`/api/generation/jobs/${jobId}/cancel`, { method: "POST" }, true),
  listActivity: (workspaceId: string, page = 1, pageSize = 50, status?: string) => {
    const params = new URLSearchParams({ workspaceId, page: String(page), pageSize: String(pageSize) });
    if (status && status !== "All") params.set("status", status);
    return request<ActivityList>(`/api/activity?${params.toString()}`);
  },
  search: (query: string, limit = 8) => request<GlobalSearchResponse>(`/api/search?q=${encodeURIComponent(query)}&limit=${limit}`),
  getActivityUnreadCount: (workspaceId: string) => request<{ unreadCount: number }>(`/api/activity/unread-count?workspaceId=${encodeURIComponent(workspaceId)}`),
  markActivityRead: (workspaceId: string, jobId: string) => request<{ read: boolean }>(`/api/activity/${jobId}/read`, { method: "POST", body: JSON.stringify({ workspaceId }) }, true),
  markAllActivityRead: (workspaceId: string) => request<{ read: boolean }>("/api/activity/read-all", { method: "POST", body: JSON.stringify({ workspaceId }) }, true),
  listAssets: (workspaceId: string, filters: AssetFilters = {}) => {
    const params = new URLSearchParams({ workspaceId, status: filters.status ?? "Active", page: String(filters.page ?? 1), pageSize: String(filters.pageSize ?? 24) });
    if (filters.projectId) params.set("projectId", filters.projectId);
    if (filters.assetType) params.set("assetType", filters.assetType);
    if (filters.search?.trim()) params.set("search", filters.search.trim());
    return request<AssetList>(`/api/assets?${params.toString()}`);
  },
  getAsset: (assetId: string) => request<Asset>(`/api/assets/${assetId}`),
  updateAsset: (assetId: string, input: AssetInput) => request<Asset>(`/api/assets/${assetId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  archiveAsset: (assetId: string) => request<Asset>(`/api/assets/${assetId}/archive`, { method: "POST" }, true),
  restoreAsset: (assetId: string) => request<Asset>(`/api/assets/${assetId}/restore`, { method: "POST" }, true),
  assetFileUrl,
  assetRepresentationUrl,
  downloadAssetRepresentation: async (assetId: string, representationId: string) => {
    const response = await fetch(assetRepresentationUrl(assetId, representationId), { credentials: "include" });
    if (!response.ok) throw new ApiError(response.status, "Asset representation unavailable.", undefined, "ASSET_REPRESENTATION_UNAVAILABLE");
    return response.blob();
  },
  listMemories: (workspaceId: string) => request<PersonalMemory[]>(`/api/workspaces/${workspaceId}/memories`),
  createMemory: (workspaceId: string, input: PersonalMemoryInput) => request<PersonalMemory>(`/api/workspaces/${workspaceId}/memories`, { method: "POST", body: JSON.stringify(input) }, true),
  updateMemory: (memoryId: string, input: PersonalMemoryInput) => request<PersonalMemory>(`/api/memories/${memoryId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  deleteMemory: (memoryId: string) => request<void>(`/api/memories/${memoryId}`, { method: "DELETE" }, true),
  listFiles: (workspaceId: string, projectId?: string, conversationId?: string) => {
    const params = new URLSearchParams();
    if (projectId) params.set("projectId", projectId);
    if (conversationId) params.set("conversationId", conversationId);
    const query = params.toString();
    return request<StoredFile[]>(`/api/workspaces/${workspaceId}/files${query ? `?${query}` : ""}`);
  },
  uploadFile: (workspaceId: string, file: File, scope: { projectId?: string; conversationId?: string } = {}) => {
    const form = new FormData();
    form.append("file", file);
    if (scope.projectId) form.append("projectId", scope.projectId);
    if (scope.conversationId) form.append("conversationId", scope.conversationId);
    return requestForm<StoredFile>(`/api/workspaces/${workspaceId}/files`, form, true);
  },
  deleteFile: (fileId: string) => request<void>(`/api/files/${fileId}`, { method: "DELETE" }, true),
};
