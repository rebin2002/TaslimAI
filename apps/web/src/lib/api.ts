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
  onboardingCompletedAt: string | null;
  onboardingIntent: OnboardingIntent | null;
  isAdmin: boolean;
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
export type OnboardingIntent = "project" | "chat" | "image" | "document" | "presentation" | "research";
export type OnboardingInput = {
  displayName?: string;
  preferredLanguage: "en" | "ar" | "ku";
  defaultGenerationLanguage: "en" | "ar" | "ku";
  intent?: OnboardingIntent;
};
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
export type AdminCountBreakdown = { key: string; count: number };
export type AdminGenerationOverview = {
  totalJobsInRange: number;
  byStatus: AdminCountBreakdown[];
  byStudio: AdminCountBreakdown[];
  recentFailures: { jobId: string; jobType: string; errorCode: string | null; failedAt: string }[];
  runningJobs: { jobId: string; jobType: string; progressPercent: number; queuedAt: string | null; startedAt: string | null; createdAt: string; retryCount: number; claimExpiresAt: string | null; isLongRunning: boolean }[];
  queuedOrPendingCount: number;
  longRunningJobCount: number;
  totalRetryCount: number;
};
export type AdminSanitizedGenerationFailure = { jobType: string; errorCode: string; occurredAt: string };
export type AdminProviderHealth = {
  key: string;
  category: string;
  status: "disabled" | "unconfigured" | "recent_operational_failure" | "operational" | "available_unknown" | string;
  enabled: boolean;
  configured: boolean;
  recentSuccessCount: number;
  recentFailureCount: number;
  averageLatencyMs: number | null;
  rateLimitEventCount: number;
  timeoutEventCount: number;
  qualityControlFailureCount: number;
  retryCount: number;
  fallbackCount: number;
  fallbackTelemetryRecorded: boolean;
  estimatedProviderCostUsd: number;
  actualProviderCostUsd: number;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  lastFailureCode: string | null;
  recentFailures: AdminSanitizedGenerationFailure[];
};
export type AdminUsageOperations = {
  requestCount: number;
  completedRequestCount: number;
  failedRequestCount: number;
  pendingRequestCount: number;
  inputTokens: number;
  cachedInputTokens: number;
  outputTokens: number;
  imageInputTokens: number;
  imageOutputTokens: number;
  providerCostUsd: number;
  customerChargesUsd: number;
  pendingEstimatedProviderCostUsd: number;
  byFeature: { feature: string; requestCount: number; completedRequestCount: number; failedRequestCount: number; pendingRequestCount: number; inputTokens: number; cachedInputTokens: number; outputTokens: number; imageInputTokens: number; imageOutputTokens: number; providerCostUsd: number; customerChargesUsd: number }[];
};
export type AdminOperationsDashboard = {
  range: { fromUtc: string; toUtc: string };
  generation: AdminGenerationOverview;
  usage: AdminUsageOperations;
  usersAndWorkspaces: { totalUsers: number; activeUsers: number; disabledUsers: number; totalWorkspaces: number; personalWorkspaces: number; businessWorkspaces: number; archivedWorkspaces: number };
  assetsAndStorage: { totalAssets: number; assetsByType: AdminCountBreakdown[]; totalStoredFiles: number; storedBytes: number; filesByStatus: AdminCountBreakdown[]; filesByStorageProvider: AdminCountBreakdown[]; configuredStorageProvider: string; persistentStorageConfigured: boolean };
  billing: { customerChargingEnabled: boolean; configuredProvider: string; paymentProviderConfigured: boolean; subscriptions: { planCode: string; status: string; count: number }[]; paymentAttemptsByStatus: AdminCountBreakdown[]; pendingReconciliationCount: number };
  signals: { runningJobCount: number; queuedOrPendingJobCount: number; recentFailureCount: number; anomalousUsageCountInRange: number; lastCompletedGenerationAt: string | null };
  providers: AdminProviderHealth[];
};
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
export type CinematographyCapabilityReference = { field: string; classification: "Native" | "Translated" | "Simulated/Post" | "Unsupported"; rationale: string | null };
export type CinematographyIntentSelection = { intent?: string | null; presetId?: string | null; notes?: string | null; capabilityReferences?: CinematographyCapabilityReference[] | null; shotSize?: string | null; focalLength?: string | null; lensIntent?: string | null; apertureDepthOfField?: string | null; cameraAngle?: string | null; cameraMovement?: string | null; frameRateIntent?: string | null; lighting?: string | null; paletteLook?: string | null; compositionNotes?: string | null };
export type CinematographyPreset = CinematographyIntentSelection & { id: string; name: string; summary: string; shotSize: string; focalLength: string; lensIntent: string; apertureDepthOfField: string; cameraAngle: string; cameraMovement: string; frameRateIntent: string; lighting: string; paletteLook: string; compositionNotes: string };
export type MovieCinematographyBible = { intent: string | null; presetId: string | null; notes: string | null; capabilityReferences: CinematographyCapabilityReference[] };
export type MovieGuide = { id: string; visualLanguage: string; cameraLanguage: string; colorAndLighting: string; soundAndNarration: string; continuityRules: string; updatedAt: string; currentRevisionNumber?: number; lockedRevisionNumber?: number | null; lockedAt?: string | null; cinematographyBible?: MovieCinematographyBible | null };
export type MovieScene = { id: string; sequence: number; title: string; summary: string; durationSeconds: number | null; continuityNotes: string | null; narration: string | null; dialogue: string | null; shots: MovieShot[]; clips: MovieClip[] };
export type MovieProductionStage = "ShotPlan" | "StoryboardCandidate" | "ApprovedStoryboard" | "ProductionKeyframe" | "ApprovedKeyframe" | "MotionPreview" | "ProductionRender" | "SelectedFinalTake";
export type MovieProductionAssetReference = { assetId: string; role: string };
export type MovieProductionVersion = { id: string; movieShotId: string; versionNumber: number; stage: MovieProductionStage; status: "Draft" | "PendingApproval" | "Approved" | "Rejected" | "Selected"; label: string | null; compositionJson: string; regenerationMetadataJson: string | null; stageProvenanceJson: string | null; sourceVersionId: string | null; generationJobId: string | null; assetId: string | null; firstFrameAssetId: string | null; lastFrameAssetId: string | null; firstFrameNotes: string | null; lastFrameNotes: string | null; rejectionReason: string | null; createdAt: string; updatedAt: string; reviewedAt: string | null; assetReferences: MovieProductionAssetReference[] };
export type MovieProductionStageTransition = { id: string; movieShotId: string; movieProductionVersionId: string; fromStage: MovieProductionStage; toStage: MovieProductionStage; eventType: string; reason: string | null; metadataJson: string | null; sourceVersionId: string | null; generationJobId: string | null; actorUserId: string; createdAt: string };
export type MovieShotProduction = { movieShotId: string; currentStage: MovieProductionStage; versions: MovieProductionVersion[]; transitions: MovieProductionStageTransition[] };
export type MovieShotReadinessCheck = { key: string; label: string; satisfied: boolean; detail: string };
export type MovieShotReadiness = { ready: boolean; checks: MovieShotReadinessCheck[]; missing: string[]; summary: string };
export type MovieShotPlanState = "Draft" | "ReadyForStoryboard" | "Storyboard" | "Production" | "Archived";
export type MovieShotPlanningInput = { description: string; purpose?: string | null; subjects?: string | null; subjectCharacterIds?: string[]; locationSet?: string | null; durationSeconds?: number | null; productionRequirements?: string | null; continuityReferences?: string | null; cameraAndFraming?: string | null; cameraMotion?: string | null; narration?: string | null; dialogue?: string | null; visualContinuityNotes?: string | null; cinematography?: CinematographyIntentSelection | null };
export type MovieShot = { id: string; sequence: number; description: string; purpose: string | null; subjects: string | null; subjectCharacterIds: string[]; locationSet: string | null; durationSeconds: number | null; productionRequirements: string | null; continuityReferences: string | null; cameraAndFraming: string | null; cameraMotion: string | null; cinematographyJson: string | null; cinematographySummary: string | null; narration: string | null; dialogue: string | null; visualContinuityNotes: string | null; status: string; planState: MovieShotPlanState | string; readiness: MovieShotReadiness; productionStage: MovieProductionStage; clips: MovieClip[]; productionVersions: MovieProductionVersion[] };
export type MovieSceneShotPlan = { sceneId: string; sceneSequence: number; sceneTitle: string; sceneSummary: string; sceneDurationSeconds: number | null; sceneStatus: string; shotCount: number; activeShotCount: number; readyShotCount: number; totalDurationSeconds: number; coveragePercent: number; shots: MovieShot[] };
export type MovieCharacterState = { id: string; key: string; label: string | null; wardrobe: string | null; ageOrTimeState: string | null; appearance: string | null; injuryOrCondition: string | null; locationOrStoryState: string | null; continuityNotes: string | null; createdAt: string; updatedAt: string };
export type MovieCharacterRelationship = { id: string; relatedCharacterId: string; relatedCharacterName: string; relationshipType: string; notes: string | null };
export type MovieCharacterContinuityLock = { id: string; fieldKey: string; lockedValue: string; characterStateId: string | null; approvedAt: string };
export type MovieCharacterInput = { name: string; role?: string | null; description: string; appearance?: string | null; physicalDescription?: string | null; wardrobe?: string | null; voiceReference?: string | null; personalityAndStoryNotes?: string | null; voiceAndPerformance?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null; referenceAssetIds?: string[] };
export type MovieCharacter = { id: string; name: string; role: string | null; description: string; appearance: string | null; physicalDescription: string | null; wardrobe: string | null; voiceReference: string | null; personalityAndStoryNotes: string | null; voiceAndPerformance: string | null; continuityNotes: string | null; referenceAssetId: string | null; referenceAssetIds: string[]; states: MovieCharacterState[]; relationships: MovieCharacterRelationship[]; continuityLocks: MovieCharacterContinuityLock[] };
export type MovieLocation = { id: string; name: string; description: string; visualContinuityNotes: string | null; referenceAssetId: string | null };
export type MovieClip = { id: string; movieSceneId: string | null; movieShotId: string | null; generationJobId: string | null; assetId: string | null; status: string; durationSeconds: number | null; metadataJson: string | null; continuitySnapshotJson: string | null };
export type MovieAssembly = { id: string; generationJobId: string | null; assetId: string | null; status: string; outputFormat: string; metadataJson: string | null; createdAt: string; completedAt: string | null };
export type MovieSetVariation = { id: string; movieSetId: string; name: string; visualDescription: string | null; timeOfDay: string | null; weather: string | null; lighting: string | null; continuityNotes: string | null; referenceAssetId: string | null; isDefault: boolean };
export type MovieSet = { id: string; movieLocationId: string | null; name: string; description: string; environmentType: string; visualDescription: string | null; timeOfDay: string | null; weather: string | null; continuityNotes: string | null; referenceAssetId: string | null; variations: MovieSetVariation[] };
export type MovieProp = { id: string; name: string; description: string; category: string | null; continuityNotes: string | null; referenceAssetId: string | null };
export type MovieWorldReference = { id: string; name: string; kind: string; description: string | null; tagsJson: string | null; assetId: string | null };
export type MovieWorldUsage = { id: string; movieSceneId: string; movieShotId: string | null; entityType: string; entityId: string; role: string | null };
export type MovieContinuityFact = { id: string; scopeType: string; scopeId: string | null; factKey: string; factValue: string; notes: string | null; updatedAt: string };
export type MovieContinuityLock = { id: string; entityType: string; entityId: string | null; fieldName: string; lockedValue: string; strength: string; reason: string | null; createdAt: string; releasedAt: string | null };
export type MovieWorld = { locations: MovieLocation[]; sets: MovieSet[]; props: MovieProp[]; references: MovieWorldReference[]; usages: MovieWorldUsage[]; facts: MovieContinuityFact[]; locks: MovieContinuityLock[] };
export type MovieProject = { id: string; workspaceId: string; projectId: string | null; mode: "Quick" | "Full"; status: string; title: string; description: string; durationSeconds: number; aspectRatio: string; style: string; language: string; additionalInstructions: string | null; createdAt: string; updatedAt: string; guide: MovieGuide; scenes: MovieScene[]; characters: MovieCharacter[]; locations: MovieLocation[]; clips: MovieClip[]; assemblies: MovieAssembly[]; world: MovieWorld };
export type MovieProviderReadiness = { ready: boolean; supportedOperations: string[] };
export type MovieStudioResponse = { project: MovieProject; job: GenerationJob | null };
export type MovieStudioCreateInput = { workspaceId: string; projectId?: string | null; mode: "Quick" | "Full"; title: string; description: string; durationSeconds: number; aspectRatio: string; style: string; language: string; additionalInstructions?: string | null; visualLanguage?: string | null; cameraLanguage?: string | null; colorAndLighting?: string | null; soundAndNarration?: string | null; continuityRules?: string | null; cinematography?: CinematographyIntentSelection | null };
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
export type NotificationItem = {
  id: string;
  workspaceId: string;
  projectId: string | null;
  generationJobId: string | null;
  assetId: string | null;
  type: "generation.completed" | "generation.failed" | "generation.attention" | "billing.payment_failed";
  resourceTitle: string | null;
  createdAt: string;
  readAt: string | null;
  isRead: boolean;
  destination: string;
};
export type NotificationList = { items: NotificationItem[]; page: number; pageSize: number; totalCount: number; totalPages: number; unreadCount: number };
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
  fileSizeBytes: number | null;
  sourceStudio: string | null;
  sourceJobTitle: string | null;
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
  representations: AssetRepresentation[];
};
export type AssetRepresentation = { id: string; representationType: string; fileName: string; contentType: string; sizeBytes: number; createdAt: string };
export type AssetList = { items: Asset[]; page: number; pageSize: number; totalCount: number; totalPages: number };
export type AssetSort = "recent" | "oldest" | "name" | "size";
export type AssetFilters = { projectId?: string; assetType?: AssetType; status?: AssetStatus; search?: string; sort?: AssetSort; page?: number; pageSize?: number };
export type AssetInput = { name: string; description?: string | null; projectId?: string | null };

let csrfToken: string | null = null;

function requestId() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

function generationInit(init: RequestInit, idempotencyKey: string): RequestInit {
  const headers = new Headers(init.headers);
  headers.set("Idempotency-Key", idempotencyKey);
  return { ...init, headers };
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

async function streamRequest(path: string, payload: unknown, onEvent: (event: ChatStreamEvent) => void, signal?: AbortSignal, retryCsrf = true): Promise<void> {
  const headers = new Headers({ "Content-Type": "application/json", Accept: "text/event-stream" });
  headers.set("X-CSRF-TOKEN", csrfToken ?? await csrf());
  const response = await fetch(`${API_URL}${path}`, { method: "POST", headers, credentials: "include", body: JSON.stringify(payload), signal });
  if (!response.ok) {
    const body = await parseError(response);
    if (response.status === 400 && retryCsrf && body?.error?.code === "CSRF_VALIDATION_FAILED") {
      csrfToken = null;
      await csrf(true);
      return streamRequest(path, payload, onEvent, signal, false);
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
  completeOnboarding: (input: OnboardingInput) => request<AuthResponse>("/api/auth/onboarding/complete", { method: "POST", body: JSON.stringify(input) }, true),
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
  deleteConversation: (conversationId: string) => request<void>(`/api/conversations/${conversationId}`, { method: "DELETE" }, true),
  sendMessage: (conversationId: string, content: string, id = requestId(), attachmentIds: string[] = []) => request<SendMessageResponse>(`/api/conversations/${conversationId}/messages`, { method: "POST", body: JSON.stringify({ content, requestId: id, attachmentIds }) }, true),
  streamMessage: (conversationId: string, content: string, onEvent: (event: ChatStreamEvent) => void, id = requestId(), attachmentIds: string[] = [], signal?: AbortSignal) => streamRequest(`/api/conversations/${conversationId}/messages/stream`, { content, requestId: id, attachmentIds }, onEvent, signal),
  regenerateMessage: (conversationId: string, messageId: string, onEvent: (event: ChatStreamEvent) => void, id = requestId(), signal?: AbortSignal) => streamRequest(`/api/conversations/${conversationId}/messages/${messageId}/regenerate`, { requestId: id }, onEvent, signal),
  getUsageSummary: (workspaceId: string) => request<UsageSummary>(`/api/workspaces/${workspaceId}/usage/summary`),
  getUsageHistory: (workspaceId: string, page = 1, pageSize = 20) => request<UsageHistory>(`/api/workspaces/${workspaceId}/usage?page=${page}&pageSize=${pageSize}`),
  getBillingAccount: (workspaceId: string) => request<BillingAccount>(`/api/workspaces/${workspaceId}/billing`),
  getAdminUsageReport: (params: Record<string, string | number | undefined> = {}) => {
    const query = new URLSearchParams(Object.entries(params).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)]));
    return request<AdminUsageReport>(`/api/admin/usage/report${query.toString() ? `?${query.toString()}` : ""}`);
  },
  getAdminUsageTransaction: (id: string) => request<AdminUsageTransaction>(`/api/admin/usage/transactions/${id}`),
  getAdminOperationsDashboard: (params: Record<string, string | number | undefined> = {}) => {
    const query = new URLSearchParams(Object.entries(params).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)]));
    return request<AdminOperationsDashboard>(`/api/admin/operations/dashboard${query.toString() ? `?${query.toString()}` : ""}`);
  },
  createGenerationJob: (workspaceId: string, inputJson = "{}", title?: string, idempotencyKey = requestId()) => request<GenerationJob>("/api/generation/jobs", generationInit({ method: "POST", body: JSON.stringify({ workspaceId, jobType: "system.test", inputJson, title }) }, idempotencyKey), true),
  createImageGenerationJob: (input: ImageGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/image-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createVoiceGenerationJob: (input: VoiceGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/voice-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createDocumentGenerationJob: (input: DocumentGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/document-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createPresentationGenerationJob: (input: PresentationGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/presentation-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createResearchGenerationJob: (input: ResearchGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/research-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createSocialGenerationJob: (input: SocialGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/social-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  getMovieProvider: () => request<{ provider: MovieProviderReadiness }>("/api/movie-studio/provider"),
  getCinematographyPresets: () => request<CinematographyPreset[]>("/api/movie-studio/cinematography/presets"),
  createMovieProject: (input: MovieStudioCreateInput) => request<MovieStudioResponse>("/api/movie-studio/projects", { method: "POST", body: JSON.stringify(input) }, true),
  getMovieProject: (id: string) => request<MovieProject>(`/api/movie-studio/projects/${id}`),
  updateMovieGuide: (id: string, input: Partial<Omit<MovieGuide, "cinematographyBible">> & { cinematography?: CinematographyIntentSelection | null }) => request<MovieProject>(`/api/movie-studio/projects/${id}/guide`, { method: "PATCH", body: JSON.stringify(input) }, true),
  addMovieScene: (id: string, input: { title: string; summary: string; durationSeconds?: number | null; continuityNotes?: string | null; narration?: string | null; dialogue?: string | null }) => request<MovieScene>(`/api/movie-studio/projects/${id}/scenes`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieCharacter: (id: string, input: MovieCharacterInput) => request<MovieCharacter>(`/api/movie-studio/projects/${id}/characters`, { method: "POST", body: JSON.stringify(input) }, true),
  updateMovieCharacter: (characterId: string, input: MovieCharacterInput) => request<MovieCharacter>(`/api/movie-studio/characters/${characterId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  addMovieCharacterState: (characterId: string, input: Omit<MovieCharacterState, "id" | "createdAt" | "updatedAt">) => request<MovieCharacterState>(`/api/movie-studio/characters/${characterId}/states`, { method: "POST", body: JSON.stringify(input) }, true),
  updateMovieCharacterState: (stateId: string, input: Omit<MovieCharacterState, "id" | "key" | "createdAt" | "updatedAt"> & { key?: string }) => request<MovieCharacterState>(`/api/movie-studio/character-states/${stateId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  addMovieCharacterRelationship: (characterId: string, input: { relatedCharacterId: string; relationshipType: string; notes?: string | null }) => request<MovieCharacterRelationship>(`/api/movie-studio/characters/${characterId}/relationships`, { method: "POST", body: JSON.stringify(input) }, true),
  lockMovieCharacterFact: (characterId: string, input: { fieldKey: string; lockedValue: string; characterStateId?: string | null }) => request<MovieCharacterContinuityLock>(`/api/movie-studio/characters/${characterId}/continuity-locks`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieShot: (sceneId: string, input: MovieShotPlanningInput) => request<MovieShot>(`/api/movie-studio/scenes/${sceneId}/shots`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieSceneShotPlan: (sceneId: string) => request<MovieSceneShotPlan>(`/api/movie-studio/scenes/${sceneId}/shots`),
  getMovieShotPlanning: (shotId: string) => request<MovieShot>(`/api/movie-studio/shots/${shotId}`),
  updateMovieShot: (shotId: string, input: MovieShotPlanningInput & { status?: string | null }) => request<MovieShot>(`/api/movie-studio/shots/${shotId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  reorderMovieShot: (shotId: string, sequence: number) => request<MovieSceneShotPlan>(`/api/movie-studio/shots/${shotId}/reorder`, { method: "POST", body: JSON.stringify({ sequence }) }, true),
  archiveMovieShot: (shotId: string) => request<MovieSceneShotPlan>(`/api/movie-studio/shots/${shotId}/archive`, { method: "POST" }, true),
  addMovieLocation: (id: string, input: { name: string; description: string; visualContinuityNotes?: string | null; referenceAssetId?: string | null }) => request<MovieLocation>(`/api/movie-studio/projects/${id}/locations`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieShotProduction: (shotId: string) => request<MovieShotProduction>(`/api/movie-studio/shots/${shotId}/production`),
  createMovieProductionVersion: (shotId: string, input: { stage: MovieProductionStage; label?: string | null; compositionJson: string; regenerationMetadataJson?: string | null; stageProvenanceJson?: string | null; sourceVersionId?: string | null; generationJobId?: string | null; assetId?: string | null; firstFrameAssetId?: string | null; lastFrameAssetId?: string | null; firstFrameNotes?: string | null; lastFrameNotes?: string | null; assetReferences?: MovieProductionAssetReference[] }, idempotencyKey = requestId()) => request<MovieProductionVersion>(`/api/movie-studio/shots/${shotId}/production/versions`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  reviewMovieProductionVersion: (versionId: string, input: { approve: boolean; reason?: string | null; metadataJson?: string | null }) => request<MovieProductionVersion>(`/api/movie-studio/production/versions/${versionId}/review`, { method: "POST", body: JSON.stringify(input) }, true),
  generateMovieScene: (projectId: string, sceneId: string, input: { title?: string | null } = {}, idempotencyKey = requestId()) => request<{ project: MovieProject; job: GenerationJob; clipId: string }>(`/api/movie-studio/projects/${projectId}/scenes/${sceneId}/generate`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  generateMovieShot: (shotId: string, input: { title?: string | null } = {}, idempotencyKey = requestId()) => request<{ project: MovieProject; job: GenerationJob; clipId: string }>(`/api/movie-studio/shots/${shotId}/generate`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  createMusicGenerationJob: (input: MusicGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/music-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
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
  listNotifications: (workspaceId: string, page = 1, pageSize = 20, unreadOnly = false) => {
    const params = new URLSearchParams({ workspaceId, page: String(page), pageSize: String(pageSize), unreadOnly: String(unreadOnly) });
    return request<NotificationList>(`/api/notifications?${params.toString()}`);
  },
  getNotificationUnreadCount: (workspaceId: string) => request<{ unreadCount: number }>(`/api/notifications/unread-count?workspaceId=${encodeURIComponent(workspaceId)}`),
  markNotificationRead: (workspaceId: string, notificationId: string) => request<{ read: boolean }>(`/api/notifications/${notificationId}/read`, { method: "POST", body: JSON.stringify({ workspaceId }) }, true),
  markAllNotificationsRead: (workspaceId: string) => request<{ read: boolean }>("/api/notifications/read-all", { method: "POST", body: JSON.stringify({ workspaceId }) }, true),
  listAssets: (workspaceId: string, filters: AssetFilters = {}) => {
    const params = new URLSearchParams({ workspaceId, status: filters.status ?? "Active", page: String(filters.page ?? 1), pageSize: String(filters.pageSize ?? 24) });
    if (filters.projectId) params.set("projectId", filters.projectId);
    if (filters.assetType) params.set("assetType", filters.assetType);
    if (filters.search?.trim()) params.set("search", filters.search.trim());
    if (filters.sort) params.set("sort", filters.sort);
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
