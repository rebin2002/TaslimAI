import { createSseParser, type TaslimSseEvent } from "./sse";
import { API_URL, assetFileUrl, assetRepresentationUrl } from "./apiBase";
import { awaitWithChatStreamWatchdog, CHAT_STREAM_WATCHDOG_TIMEOUT_MS, ChatStreamTransportError } from "./chatStreamTransport";

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
  failuresByCode: AdminCountBreakdown[];
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
  movie: AdminMovieOperations;
  workers: AdminWorkerOperations;
  recentAdminActions: AdminOperationAudit[];
};
export type AdminMovieOperations = {
  movieJobCountInRange: number;
  jobsByStatus: AdminCountBreakdown[];
  queuedOrPendingCount: number;
  oldestQueuedAt: string | null;
  oldestQueueAgeSeconds: number | null;
  averageQueueAgeSeconds: number | null;
  totalRetryCount: number;
  retriedJobCount: number;
  maxRetryCount: number;
  failuresByCode: AdminCountBreakdown[];
  providerDisabledFailureCount: number;
  qualityControlFailureCount: number;
  qualityControlByStatus: AdminCountBreakdown[];
  movieAssetCount: number;
  movieAssetWithStoredFileCount: number;
  movieAssetIngestionGapCount: number;
  completedJobsWithoutAssetCount: number;
  accountingTransactionCount: number;
  pendingAccountingCount: number;
  missingAccountingEvidenceCount: number;
  estimatedProviderCostUsd: number;
  actualProviderCostUsd: number;
  providerStatus: string;
  providerEnabled: boolean;
  providerConfigured: boolean;
  stuckJobCount: number;
  stuckJobs: { jobId: string; jobType: string; status: string; claimExpiresAt: string | null; startedAt: string | null; retryCount: number; errorCode: string | null }[];
};
export type AdminWorkerOperations = {
  configuredConcurrency: number;
  observedWorkerCount: number;
  healthyWorkerCount: number;
  staleWorkerCount: number;
  workers: { workerId: string; status: string; startedAt: string; lastSeenAt: string; lastClaimedAt: string | null; lastCompletedAt: string | null; activeJobId: string | null; consecutiveIterationFailures: number; workerConcurrency: number; isStale: boolean }[];
};
export type AdminOperationAudit = { id: string; actorUserId: string; action: string; targetType: string; targetId: string | null; outcome: string; reason: string | null; createdAt: string };
export type GenerationJobStatus = "Pending" | "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled";
export type GenerationJobOutput = { id: string; outputType: string; storedFileId: string | null; metadataJson: string | null; createdAt: string };
export type GenerationCostWarning = { code: string; severity: "warning" | "error" | string; message: string };
export type GenerationCostCap = { scope: "workspace" | "user" | "project" | string; limitUsd: number | null; usedUsd: number; usageKnown: boolean; remainingUsd: number | null; wouldExceed: boolean };
export type GenerationCostPreview = {
  estimateStatus: "known" | "unknown" | string;
  estimatedProviderCostUsd: number | null;
  estimatedProviderCostKnown: boolean;
  currency: string;
  unknownReason: string | null;
  warning: boolean;
  confirmationRequired: boolean;
  canProceed: boolean;
  workspaceCap: GenerationCostCap | null;
  userCap: GenerationCostCap | null;
  projectCap: GenerationCostCap | null;
  warnings: GenerationCostWarning[];
};
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
export type CinematographyIntentSelection = { intent?: string | null; presetId?: string | null; notes?: string | null; capabilityReferences?: CinematographyCapabilityReference[] | null; shotSize?: string | null; focalLength?: string | null; lensIntent?: string | null; apertureDepthOfField?: string | null; cameraAngle?: string | null; cameraMovement?: string | null; frameRateIntent?: string | null; lighting?: string | null; paletteLook?: string | null; compositionNotes?: string | null; exposureLook?: string | null; continuityConstraints?: string[] | null };
export type CinematographyShotPlan = { shotSize: string; framing: string; cameraAngle: string; cameraPosition: string; cameraMovement: string; compositionIntent: string; lensLookIntent: string; focalLengthIntent?: string | null; depth: string; focusIntent: string; lightingIntent: string; exposureLook?: string | null; subjectEmphasis: string; visualTransitionIntent: string; creativeNotes?: string | null; grounding?: { source: string; evidence: string; locked: boolean }[] | null; lockedGuideRevisionNumber?: number | null; intent?: string | null; presetId?: string | null; continuityConstraints?: string[] | null; profileSource?: string | null; userOverrideFields?: string[] | null; lockedCanonFields?: string[] | null };
export type CinematographyCameraProfileOverride = { shotSize?: string | null; cameraAngle?: string | null; cameraMovement?: string | null; focalLengthIntent?: string | null; lensIntent?: string | null; depthOfField?: string | null; lightingIntent?: string | null; exposureLook?: string | null; continuityConstraints?: string[] | null };
export type CinematographyOverrideAudit = { requested: boolean; requestedFields: string[]; appliedFields: string[]; blockedByLockedCanonFields: string[]; lockedGuideRevisionNumber: number | null };
export type MovieCameraProfile = { shotSize: string; angle: string; movement: string; focalIntent: string; lensIntent: string; depthOfField: string; lightingIntent: string; exposureLook: string; continuityConstraints: string[]; source: string; userOverrideApplied: boolean; userOverrideFields: string[]; lockedFields: string[]; lockedGuideRevisionNumber: number | null };
export type MovieCinematographyPlanResponse = { movieProjectId: string; sceneId: string; shotId: string; aspectRatio: string; guideGrounded: boolean; canonPreserved: boolean; lockedGuideRevisionNumber: number | null; appliedCanonFields: string[]; plan: CinematographyShotPlan; overrideAudit: CinematographyOverrideAudit | null; cameraProfile: MovieCameraProfile | null };
export type CinematographyPreset = CinematographyIntentSelection & { id: string; name: string; summary: string; shotSize: string; focalLength: string; lensIntent: string; apertureDepthOfField: string; cameraAngle: string; cameraMovement: string; frameRateIntent: string; lighting: string; paletteLook: string; compositionNotes: string };
export type MovieCinematographyBible = { intent: string | null; presetId: string | null; notes: string | null; capabilityReferences: CinematographyCapabilityReference[] };
export type MovieGuide = { id: string; visualLanguage: string; cameraLanguage: string; colorAndLighting: string; soundAndNarration: string; continuityRules: string; updatedAt: string; currentRevisionNumber?: number; lockedRevisionNumber?: number | null; lockedAt?: string | null; cinematographyBible?: MovieCinematographyBible | null };
export type MovieScene = { shotCount?: number; id: string; sequence: number; title: string; summary: string; durationSeconds: number | null; continuityNotes: string | null; narration: string | null; dialogue: string | null; shots: MovieShot[]; clips: MovieClip[] };
export type MovieProductionStage = "ShotPlan" | "StoryboardCandidate" | "ApprovedStoryboard" | "ProductionKeyframe" | "ApprovedKeyframe" | "MotionPreview" | "ProductionRender" | "SelectedFinalTake";
export type MovieProductionAssetReference = { assetId: string; role: string };
export type MovieProductionVersionInput = { stage: MovieProductionStage; label?: string | null; compositionJson: string; regenerationMetadataJson?: string | null; stageProvenanceJson?: string | null; sourceVersionId?: string | null; generationJobId?: string | null; assetId?: string | null; firstFrameAssetId?: string | null; lastFrameAssetId?: string | null; firstFrameNotes?: string | null; lastFrameNotes?: string | null; assetReferences?: MovieProductionAssetReference[] };
export type MovieKeyframeGenerationInput = { sourceStoryboardVersionId: string; label?: string | null; compositionJson?: string | null; regenerationMetadataJson?: string | null };
export type MovieProductionReviewInput = { approve: boolean; reason?: string | null; metadataJson?: string | null };
export type MovieProductionProviderAttempt = { attemptNumber: number; retryNumber: number; isRetry: boolean; isFallback: boolean; status: string; resultClassification: string; failureCode: string | null; rateLimited: boolean; timedOut: boolean; circuitOpen: boolean; qualityControlRejected: boolean; startedAt: string; completedAt: string | null };
export type MovieProductionExecution = { generationJobId: string; jobType: string; status: string; progressPercent: number; retryCount: number; attemptCount: number; qualityControlStatus: string; errorCode: string | null; errorMessage: string | null; assetId: string | null; assetType: string | null; attempts: MovieProductionProviderAttempt[] };
export type MovieProductionPreflightCheck = { key: string; label: string; satisfied: boolean; required: boolean; detail: string };
export type MovieProductionPreflightRecommendation = { key: string; label: string; detail: string; suggestedStage?: MovieProductionStage | null };
export type MovieProductionAdaptiveResolution = { sourceResolution: string; targetResolution: string; processingPath: string; qualityTier: string; qualityConfidence: number; minimumQualityConfidence: number; qcEscalationRequired: boolean; escalateToSourceResolution: string | null; reasonCodes: string[] };
export type MovieProductionPreflight = { schemaVersion: number; movieShotId: string; sourceProductionVersionId: string | null; isExpensiveProduction: boolean; canProceed: boolean; decision: string; summary: string; checks: MovieProductionPreflightCheck[]; recommendations: MovieProductionPreflightRecommendation[]; adaptiveResolution: MovieProductionAdaptiveResolution; costGuardrails: GenerationCostPreview; rejectionCode: string | null; rejectionMessage: string | null };
export type MovieTakeApproval = { id: string; userId: string; decision: string; comment: string | null; createdAt: string };
export type MovieTake = { id: string; movieShotId: string; versionNumber: number; label: string; status: string; qualityLevel: string; autoDirectorEnabled: boolean; movieClipId: string | null; generationJobId: string | null; assetId: string | null; notes: string | null; selectedAt: string | null; finalizedAt: string | null; createdAt: string; updatedAt: string; approvals: MovieTakeApproval[]; execution: MovieProductionExecution | null };
export type MovieTimelineItem = { id: string; sequence: number; kind: string; sourceTakeId: string | null; sourceAssetId: string | null; timelineInMilliseconds: number; timelineOutMilliseconds: number; sourceInMilliseconds: number | null; sourceOutMilliseconds: number | null; durationMilliseconds: number; label: string | null; metadataJson: string | null; isGap: boolean };
export type MovieTimelineTrack = { id: string; trackNumber: number; kind: string; name: string | null; isMuted: boolean; items: MovieTimelineItem[] };
export type MovieTimelineRevision = { id: string; movieProjectId: string; revisionNumber: number; baseRevisionId: string | null; status: string; label: string | null; changeSummary: string | null; durationMilliseconds: number; createdAt: string; updatedAt: string; lockedAt: string | null; lockedByUserId: string | null; tracks: MovieTimelineTrack[] };
export type MovieTimeline = { id: string; movieProjectId: string; currentRevisionNumber: number; currentRevisionId: string | null; lockedRevisionNumber: number | null; lockedRevisionId: string | null; revisions: MovieTimelineRevision[]; currentRevision: MovieTimelineRevision | null };
export type MovieTimelineRevisionRequest = { baseRevisionId?: string | null; label?: string | null; changeSummary?: string | null; tracks?: Array<{ kind: string; name?: string | null; trackNumber?: number | null; isMuted?: boolean; items: Array<{ kind: string; sourceTakeId?: string | null; sourceAssetId?: string | null; timelineInMilliseconds: number; timelineOutMilliseconds?: number | null; sourceInMilliseconds?: number | null; sourceOutMilliseconds?: number | null; label?: string | null; metadataJson?: string | null }> }> };
export type MovieTakeUpscaleEligibility = { takeId: string; movieShotId: string; eligible: boolean; isSelected: boolean; isFinal: boolean; code: string; message: string; targetMasterResolution: string; sourceResolution: string | null; auditId: string | null; auditStatus: string | null; evaluatedAt: string };
export type MovieFinalMaster = { id: string; movieProjectId: string; movieShotId: string; sourceTakeId: string; sourceMovieClipId: string | null; sourceAssetId: string | null; outputAssetId: string | null; generationJobId: string | null; targetProfile: string; sourceWidth: number | null; sourceHeight: number | null; targetWidth: number; targetHeight: number; state: string; stateReason: string | null; qcStatus: string; qcResultJson: string | null; provenanceJson: string | null; supersedesMasterId: string | null; supersededByMasterId: string | null; requestedAt: string; updatedAt: string; completedAt: string | null; supersededAt: string | null };
export type MovieProductionVersion = { id: string; movieShotId: string; versionNumber: number; stage: MovieProductionStage; status: "Draft" | "PendingApproval" | "Approved" | "Rejected" | "Selected"; label: string | null; compositionJson: string; regenerationMetadataJson: string | null; stageProvenanceJson: string | null; continuitySnapshotReferenceJson: string | null; cinematographyReferenceJson: string | null; sourceVersionId: string | null; generationJobId: string | null; assetId: string | null; firstFrameAssetId: string | null; lastFrameAssetId: string | null; firstFrameNotes: string | null; lastFrameNotes: string | null; rejectionReason: string | null; createdAt: string; updatedAt: string; reviewedAt: string | null; assetReferences: MovieProductionAssetReference[]; execution: MovieProductionExecution | null; isLocked?: boolean; lockedAt?: string | null; lockedByUserId?: string | null; isSelected?: boolean };
export type MovieRegenerationRequest = { id: string; movieShotId: string; targetType: string; targetId: string; actionType: string; requestedStage: MovieProductionStage; reason: string; sourceVersionId: string | null; changedInputsJson: string; compositionJson: string; status: string; createdByUserId: string; confirmedByUserId: string | null; generationJobId: string | null; resultingProductionVersionId: string | null; resultingTakeId: string | null; createdAt: string; confirmedAt: string | null; costPreview: { estimatedProviderCostUsd: number | null; estimatedProviderCostKnown: boolean; currency: string; costEstimateJson: string | null; confirmationRequired: boolean; guardrails: GenerationCostPreview | null } };
export type MovieSelectiveRegenerationResponse = { request: MovieRegenerationRequest; job: GenerationJob | null; productionVersion: MovieProductionVersion | null; take: MovieTake | null };
export type MovieProductionStageTransition = { id: string; movieShotId: string; movieProductionVersionId: string; fromStage: MovieProductionStage; toStage: MovieProductionStage; eventType: string; reason: string | null; metadataJson: string | null; sourceVersionId: string | null; generationJobId: string | null; actorUserId: string; createdAt: string };
export type MovieProductionCheckpointItem = { shotId: string; sceneTitle: string; sceneSequence: number; shotSequence: number; label: string; state: "Complete" | "Running" | "Blocked" | "Recoverable" | string; currentStage: MovieProductionStage | string; progressPercent: number; blockedReason: string | null; nextAction: string | null; recoveryJobId: string | null; selectedTakeId: string | null; updatedAt: string };
export type MovieProductionRecoveryAction = { actionId: string; shotId: string; sceneTitle: string; label: string; action: "Retry" | string; reason: string };
export type MovieProductionCheckpoint = { movieProjectId: string; version: number; state: "NotStarted" | "Active" | "Running" | "Blocked" | "Recoverable" | "Complete" | string; progressPercent: number; totalShots: number; completedShots: number; runningShots: number; blockedShots: number; recoverableShots: number; pendingApprovalShots: number; observedAt: string; lastRecoveredAt: string | null; items: MovieProductionCheckpointItem[]; recoveryActions: MovieProductionRecoveryAction[] };
export type MovieProductionRecoveryResponse = { checkpoint: MovieProductionCheckpoint; job: GenerationJob | null };
export type MovieWorldContinuitySource = { entityType: string; entityId: string | null; recordId: string | null; fieldName: string; value: string };
export type MovieWorldContinuityWarning = { code: string; severity: "warning" | "error"; message: string; source: MovieWorldContinuitySource; target: { scopeType: string; movieProjectId: string; sceneId: string | null; shotId: string | null } };
export type MovieWorldContinuitySnapshot = { movieProjectId: string; sceneId: string | null; shotId: string | null; snapshotVersion: number; createdAt: string; snapshotHash: string; locations: MovieLocation[]; sets: MovieSet[]; props: (MovieProp & { state: string | null })[]; facts: MovieContinuityFact[]; locks: MovieContinuityLock[]; warnings: MovieWorldContinuityWarning[] };
export type MovieShotProduction = { movieShotId: string; currentStage: MovieProductionStage; versions: MovieProductionVersion[]; transitions: MovieProductionStageTransition[]; worldContinuity?: MovieWorldContinuitySnapshot | null; takes: MovieTake[]; regenerationRequests: MovieRegenerationRequest[]; selectedKeyframeVersionId?: string | null };
export type MovieShotReadinessCheck = { key: string; label: string; satisfied: boolean; detail: string };
export type MovieShotReadiness = { ready: boolean; checks: MovieShotReadinessCheck[]; missing: string[]; summary: string };
export type MovieShotPlanState = "Draft" | "ReadyForStoryboard" | "Storyboard" | "Production" | "Archived";
export type MovieShotPlanningInput = { description: string; purpose?: string | null; subjects?: string | null; subjectCharacterIds?: string[]; locationSet?: string | null; durationSeconds?: number | null; productionRequirements?: string | null; continuityReferences?: string | null; cameraAndFraming?: string | null; cameraMotion?: string | null; narration?: string | null; dialogue?: string | null; visualContinuityNotes?: string | null; cinematography?: CinematographyIntentSelection | null };
export type MovieShot = { id: string; sequence: number; description: string; purpose: string | null; subjects: string | null; subjectCharacterIds: string[]; locationSet: string | null; durationSeconds: number | null; productionRequirements: string | null; continuityReferences: string | null; cameraAndFraming: string | null; cameraMotion: string | null; cinematographyJson: string | null; cinematographySummary: string | null; narration: string | null; dialogue: string | null; visualContinuityNotes: string | null; status: string; planState: MovieShotPlanState | string; readiness: MovieShotReadiness; productionStage: MovieProductionStage; clips: MovieClip[]; productionVersions: MovieProductionVersion[]; takes: MovieTake[]; selectedKeyframeVersionId?: string | null; cinematographyPlan?: CinematographyShotPlan | null; cameraProfile?: MovieCameraProfile | null };
export type MovieSceneShotPlan = { sceneId: string; sceneSequence: number; sceneTitle: string; sceneSummary: string; sceneDurationSeconds: number | null; sceneStatus: string; shotCount: number; activeShotCount: number; readyShotCount: number; totalDurationSeconds: number; coveragePercent: number; shots: MovieShot[] };
export type MovieStoryboardCandidate = { id: string; movieShotId: string; versionNumber: number; stage: "StoryboardCandidate" | "ApprovedStoryboard"; status: "Draft" | "PendingApproval" | "Approved" | "Rejected" | "Selected"; label: string | null; compositionJson: string; regenerationMetadataJson: string | null; stageProvenanceJson: string | null; sourceVersionId: string | null; assetId: string | null; firstFrameAssetId: string | null; lastFrameAssetId: string | null; firstFrameNotes: string | null; lastFrameNotes: string | null; rejectionReason: string | null; createdAt: string; updatedAt: string; reviewedAt: string | null; assetReferences: MovieProductionAssetReference[] };
export type MovieCinematographySummary = { cameraAndFraming: string | null; cameraMotion: string | null; intent: string | null; shotSize: string | null; focalLength: string | null; cameraAngle: string | null; lighting: string | null; paletteLook: string | null; compositionNotes: string | null; cameraProfile?: MovieCameraProfile | null };
export type MovieStoryboardShot = { id: string; sequence: number; description: string; shotPlanStatus: string; currentStage: MovieProductionStage; durationSeconds: number | null; continuityWarnings: string[]; cinematography: MovieCinematographySummary; candidates: MovieStoryboardCandidate[]; approvedCandidateId: string | null; approvalStatus: "NotStarted" | "PendingApproval" | "Rejected" | "Approved" };
export type MovieStoryboardScene = { id: string; sequence: number; title: string; summary: string; durationSeconds: number | null; continuityNotes: string | null; shots: MovieStoryboardShot[] };
export type MovieStoryboardProject = { id: string; workspaceId: string; status: string; title: string; description: string; durationSeconds: number; aspectRatio: string; style: string; language: string; guide: MovieGuide; providerReady: boolean; scenes: MovieStoryboardScene[] };
export type MovieCharacterState = { id: string; key: string; label: string | null; wardrobe: string | null; ageOrTimeState: string | null; appearance: string | null; injuryOrCondition: string | null; locationOrStoryState: string | null; continuityNotes: string | null; createdAt: string; updatedAt: string };
export type MovieCharacterRelationship = { id: string; relatedCharacterId: string; relatedCharacterName: string; relationshipType: string; notes: string | null };
export type MovieCharacterContinuityLock = { id: string; fieldKey: string; lockedValue: string; characterStateId: string | null; approvedAt: string };
export type MovieCharacterProductionSheetLook = { id: string; key: string; name: string; wardrobe: string | null; appearance: string | null; referenceNotes: string | null; referenceAssetId: string | null; sortOrder: number };
export type MovieCharacterProductionSheetVersion = { id: string; versionNumber: number; status: "Draft" | "Approved" | "Locked" | string; canonicalIdentityFaceAssetId: string | null; bodyReferenceAssetId: string | null; frontReferenceAssetId: string | null; sideReferenceAssetId: string | null; backReferenceAssetId: string | null; sourceCharacterUpdatedAt: string; provenanceHash: string; provenanceJson: string; createdByUserId: string; createdAt: string; approvedAt: string | null; approvedByUserId: string | null; lockedAt: string | null; lockedByUserId: string | null; looks: MovieCharacterProductionSheetLook[] };
export type MovieCharacterProductionSheet = { id: string; movieCharacterId: string; status: "Draft" | "Approved" | "Locked" | string; currentVersionNumber: number; currentVersion: MovieCharacterProductionSheetVersion | null; versions: MovieCharacterProductionSheetVersion[]; approvedAt: string | null; approvedByUserId: string | null; lockedAt: string | null; lockedByUserId: string | null; updatedAt: string };
export type MovieCharacterProductionSheetLookInput = { key: string; name: string; wardrobe?: string | null; appearance?: string | null; referenceNotes?: string | null; referenceAssetId?: string | null };
export type MovieCharacterProductionSheetInput = { canonicalIdentityFaceAssetId?: string | null; bodyReferenceAssetId?: string | null; frontReferenceAssetId?: string | null; sideReferenceAssetId?: string | null; backReferenceAssetId?: string | null; looks: MovieCharacterProductionSheetLookInput[]; provenanceNote?: string | null };
export type MovieCharacterInput = { name: string; role?: string | null; description: string; appearance?: string | null; physicalDescription?: string | null; wardrobe?: string | null; voiceReference?: string | null; personalityAndStoryNotes?: string | null; voiceAndPerformance?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null; referenceAssetIds?: string[] };
export type MovieCharacter = { id: string; name: string; role: string | null; description: string; appearance: string | null; physicalDescription: string | null; wardrobe: string | null; voiceReference: string | null; personalityAndStoryNotes: string | null; voiceAndPerformance: string | null; continuityNotes: string | null; referenceAssetId: string | null; referenceAssetIds: string[]; states: MovieCharacterState[]; relationships: MovieCharacterRelationship[]; continuityLocks: MovieCharacterContinuityLock[]; productionSheet?: MovieCharacterProductionSheet | null };
export type MovieCastProject = Pick<MovieProject, "id" | "workspaceId" | "projectId" | "mode" | "status" | "title" | "description" | "durationSeconds" | "aspectRatio" | "style" | "language" | "createdAt" | "updatedAt">;
export type MovieCastCharacter = { id: string; name: string; role: string | null; description: string; appearance: string | null; referenceAssetId: string | null; referenceAssetIds: string[]; referenceAssetCount: number; stateCount: number; latestState: MovieCharacterState | null; relationshipCount: number; relationshipTypes: string[]; lockedFactCount: number; lockedFieldKeys: string[]; updatedAt: string };
export type MovieCast = { project: MovieCastProject; characters: MovieCastCharacter[] };
export type MovieStoryCastSuggestion = { name: string; role: string | null; description: string; source: string; sourceType: string; isEstablished: boolean; isProposed: boolean; evidence: string[] };
export type MovieStoryCastSuggestions = { movieProjectId: string; storyRevisionId: string | null; storyRevisionStatus: string; suggestions: MovieStoryCastSuggestion[] };
export type MovieCharacterDetail = { project: MovieCastProject; character: MovieCharacter };
export type MovieLocationGeographyEntry = { label: string; description: string; orientation?: string | null; relativePosition?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null };
export type MovieLocationGeographyOpening = { label: string; kind: string; description: string; orientation?: string | null; connection?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null };
export type MovieLocationGeographyPath = { label: string; from: string; to: string; description: string; orientation?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null };
export type MovieLocationGeographyVariant = { id: string; movieLocationGeographySheetId: string; versionNumber: number; name: string; status: string; description: string | null; timeOfDay: string | null; weather: string | null; lighting: string | null; colorPalette: string | null; referenceAssetId: string | null; continuityNotes: string | null; guideRevisionNumber: number; createdAt: string; updatedAt: string; approvedAt: string | null };
export type MovieLocationGeographySheet = { id: string; movieLocationId: string; versionNumber: number; status: string; establishingReferenceAssetId: string | null; establishingReferenceNotes: string | null; wideThreeQuarterReferenceAssetId: string | null; wideThreeQuarterReferenceNotes: string | null; entrancesExits: MovieLocationGeographyOpening[]; windows: MovieLocationGeographyOpening[]; paths: MovieLocationGeographyPath[]; majorObjects: MovieLocationGeographyEntry[]; lightSources: MovieLocationGeographyEntry[]; orientationAnchors: MovieLocationGeographyEntry[]; guideRevisionNumber: number; worldBibleJson: string | null; visualBibleJson: string | null; continuitySnapshotHash: string | null; createdAt: string; updatedAt: string; approvedAt: string | null; variants: MovieLocationGeographyVariant[] };
export type MovieLocation = { id: string; name: string; description: string; visualContinuityNotes: string | null; referenceAssetId: string | null; geographySheet?: MovieLocationGeographySheet | null };
export type MovieClip = { id: string; movieSceneId: string | null; movieShotId: string | null; generationJobId: string | null; assetId: string | null; status: string; durationSeconds: number | null; metadataJson: string | null; continuitySnapshotJson: string | null };
export type MovieAssembly = { id: string; generationJobId: string | null; assetId: string | null; status: string; outputFormat: string; metadataJson: string | null; createdAt: string; completedAt: string | null };
export type MovieSetVariation = { id: string; movieSetId: string; name: string; visualDescription: string | null; timeOfDay: string | null; weather: string | null; lighting: string | null; continuityNotes: string | null; referenceAssetId: string | null; isDefault: boolean };
export type MovieSet = { id: string; movieLocationId: string | null; name: string; description: string; environmentType: string; visualDescription: string | null; timeOfDay: string | null; weather: string | null; continuityNotes: string | null; referenceAssetId: string | null; variations: MovieSetVariation[] };
export type MovieProp = { id: string; name: string; description: string; category: string | null; continuityNotes: string | null; referenceAssetId: string | null };
export type MoviePropBibleProvenance = { sourceType: string; sourceId: string | null; sourceVersion: number | null; sourceHash: string; actorUserId: string; capturedAtUtc: string };
export type MoviePropBibleReference = { id: string; assetId: string; role: string; notes: string | null; provenanceJson: string; createdAt: string };
export type MoviePropBibleVariant = { id: string; key: string; label: string; description: string | null; state: string | null; visualNotes: string | null; referenceAssetId: string | null; provenanceJson: string; createdAt: string; updatedAt: string };
export type MoviePropBibleUsage = { id: string; movieSceneId: string; movieShotId: string | null; sceneSequence: number; sceneTitle: string; shotSequence: number | null; shotDescription: string | null; role: string | null };
export type MoviePropBibleVersion = { id: string; moviePropBibleId: string; versionNumber: number; status: string; identityKey: string; role: string | null; visualIdentity: string | null; continuityRules: string | null; referencesJson: string; variantsJson: string; usagesJson: string; provenanceJson: string; contentHash: string; reviewReason: string | null; createdByUserId: string; createdAt: string; reviewedAt: string | null; reviewedByUserId: string | null; lockedAt: string | null; lockedByUserId: string | null };
export type MoviePropBible = { id: string; movieProjectId: string; moviePropId: string; prop: MovieProp; identityKey: string; role: string | null; visualIdentity: string | null; continuityRules: string | null; approvalState: string; currentVersionNumber: number; approvedVersionNumber: number | null; lockedVersionNumber: number | null; approvedAt: string | null; approvedByUserId: string | null; lockedAt: string | null; lockedByUserId: string | null; references: MoviePropBibleReference[]; variants: MoviePropBibleVariant[]; usages: MoviePropBibleUsage[]; versions: MoviePropBibleVersion[]; currentProvenance: MoviePropBibleProvenance | null; createdAt: string; updatedAt: string };
export type MovieRecurringPropCandidate = { propId: string; propName: string; isRecurring: boolean; usageCount: number; sceneCount: number; sceneIds: string[]; shotIds: string[]; reason: string };
export type MovieRecurringPropDetection = { detectorVersion: number; providerFree: boolean; evaluatedAtUtc: string; candidates: MovieRecurringPropCandidate[] };
export type MoviePropBibleCollection = { schemaVersion: number; movieProjectId: string; items: MoviePropBible[]; detection: MovieRecurringPropDetection };
export type MovieWorldReference = { id: string; name: string; kind: string; description: string | null; tagsJson: string | null; assetId: string | null };
export type MovieWorldUsage = { id: string; movieSceneId: string; movieShotId: string | null; entityType: string; entityId: string; role: string | null };
export type MovieContinuityFact = { id: string; scopeType: string; scopeId: string | null; factKey: string; factValue: string; notes: string | null; updatedAt: string };
export type MovieContinuityLock = { id: string; entityType: string; entityId: string | null; fieldName: string; lockedValue: string; strength: string; reason: string | null; createdAt: string; releasedAt: string | null };
export type MovieWorld = { locations: MovieLocation[]; sets: MovieSet[]; props: MovieProp[]; references: MovieWorldReference[]; usages: MovieWorldUsage[]; facts: MovieContinuityFact[]; locks: MovieContinuityLock[] };
export type MovieWorldAsset = { id: string; name: string; assetType: AssetType; mimeType: string | null; hasFile: boolean; canPreview: boolean };
export type MovieWorldUsageDetail = MovieWorldUsage & { sceneSequence: number; sceneTitle: string; shotSequence: number | null; shotDescription: string | null; entityName: string };
export type MovieWorldWorkspace = { movieProjectId: string; workspaceId: string; projectId: string | null; status: string; title: string; description: string; durationSeconds: number; aspectRatio: string; style: string; language: string; world: MovieWorld; usageDetails: MovieWorldUsageDetail[]; assets: MovieWorldAsset[] };
export type MovieProject = { id: string; workspaceId: string; projectId: string | null; mode: "Quick" | "Full"; status: string; title: string; description: string; durationSeconds: number; aspectRatio: string; style: string; language: string; additionalInstructions: string | null; createdAt: string; updatedAt: string; guide: MovieGuide; scenes: MovieScene[]; characters: MovieCharacter[]; locations: MovieLocation[]; clips: MovieClip[]; assemblies: MovieAssembly[]; world: MovieWorld };
export type MovieOverviewProject = Pick<MovieProject, "id" | "workspaceId" | "title" | "description" | "status" | "durationSeconds" | "aspectRatio" | "style" | "language" | "createdAt" | "updatedAt"> & { productionStatus: string; qualityLevel: string; autoDirectorEnabled: boolean };
export type MovieOverviewStage = { status: string; total: number; completed: number; percent: number | null; started: boolean };
export type MovieOverviewStory = { status: string; revisionNumber: number; screenplaySceneCount: number; hasContent: boolean };
export type MovieOverviewResource = { total: number; ready: number | null; status: string };
export type MovieOverviewCounts = { total: number; planned: number; inProgress: number; approved: number; archived: number };
export type MovieOverviewProgress = { storyboard: MovieOverviewStage; keyframe: MovieOverviewStage; production: MovieOverviewStage; selectedFinalTakes: MovieOverviewStage };
export type MovieOverviewTakes = { total: number; selected: number; finalized: number; pendingApproval: number; rejected: number };
export type MovieOverviewApprovalBucket = { pending: number; status: string };
export type MovieOverviewApprovals = { production: MovieOverviewApprovalBucket; collaborative: MovieOverviewApprovalBucket; screenplay: MovieOverviewApprovalBucket; directorProposals: MovieOverviewApprovalBucket };
export type MovieOverviewCost = { isKnown: boolean; recordedProviderCostUsd: number | null; estimatedRemainingProviderCostUsd: number | null; currency: string; note: string | null };
export type MovieOverviewAction = { key: string; label: string; reason: string; module: string; priority: string };
export type MovieOverviewWarning = { key: string; severity: string; label: string; detail: string; module: string | null; entityId: string | null; entityType: string | null };
export type MovieOverviewActivity = { key: string; label: string; detail: string; occurredAt: string; module: string | null };
export type MovieOverviewBlockedItem = { entityId: string; entityType: string; label: string; detail: string; module: string };
export type MovieOverview = { project: MovieOverviewProject; story: MovieOverviewStory; cast: MovieOverviewResource; world: MovieOverviewResource; scenes: MovieOverviewCounts; shots: MovieOverviewCounts; progress: MovieOverviewProgress; takes: MovieOverviewTakes; approvals: MovieOverviewApprovals; cost: MovieOverviewCost; latestOutputAssetId: string | null; nextActions: MovieOverviewAction[]; warnings: MovieOverviewWarning[]; blockedItems: MovieOverviewBlockedItem[]; recentActivity: MovieOverviewActivity[] };
export type MovieBudgetDirectorPlanItem = { key: string; label: string; generationUnits: number; durationSecondsPerUnit: number; rationale: string };
export type MovieBudgetDirectorRequest = { durationSeconds: number; shotCount: number; candidatePassesPerShot: number; approvedReferenceReuseCount: number; salvagedSelectCount: number; missingInsertCount: number; selectedTakeCount: number; missingInsertDurationSeconds: number; selectedOnlyMastering: boolean; naiveSourceResolution?: string; naiveTargetResolution?: string; naiveQualityTier?: string; draftSourceResolution?: string; draftTargetResolution?: string; draftQualityTier?: string; masterSourceResolution?: string; masterTargetResolution?: string; masterQualityTier?: string };
export type MovieBudgetDirectorScenario = { key: string; label: string; description: string; state: string; minimumAmountUsd: number | null; maximumAmountUsd: number | null; currency: string; generationUnits: number; components: Array<{ dimension: string; quantity: number; unit: string; minimumUsd: number | null; maximumUsd: number | null }>; reason: string | null };
export type MovieBudgetDirectorSavings = { state: string; minimumAmountUsd: number | null; maximumAmountUsd: number | null; minimumPercent: number | null; maximumPercent: number | null; isEstimate: boolean; actualSavingsAvailable: boolean; actualSavingsUsd: number | null; basis: string; reason: string | null };
export type MovieBudgetDirectorEstimate = { state: string; currency: string; naivePath: MovieBudgetDirectorScenario; optimizedPath: MovieBudgetDirectorScenario; savings: MovieBudgetDirectorSavings; plan: MovieBudgetDirectorPlanItem[]; assumptions: string[]; generatedAtUtc: string };
export type MovieProjectShell = Pick<MovieProject, "id" | "workspaceId" | "projectId" | "mode" | "status" | "title" | "description" | "durationSeconds" | "aspectRatio" | "style" | "language" | "additionalInstructions" | "createdAt" | "updatedAt"> & { lockedGuideRevisionNumber: number | null };
export type MovieScreenplayElementType = "Action" | "Dialogue" | "Parenthetical" | "Transition" | "Note";
export type MovieScreenplayElement = { id: string; ordinal: number; elementType: MovieScreenplayElementType; content: string; characterName: string | null; parenthetical: string | null };
export type MovieScreenplayScene = { id: string; ordinal: number; sceneIdentifier: string; actNumber: number | null; sequenceNumber: number | null; movieSceneId: string | null; slugline: string; synopsis: string | null; elements: MovieScreenplayElement[] };
export type MovieStoryRevision = { id: string; revisionNumber: number; parentRevisionId: string | null; createdByUserId: string; premise: string; logline: string; synopsis: string; treatment: string; status: "Draft" | "Submitted" | "Approved" | "Rejected" | "Superseded"; authorship: "Human" | "AiSuggested" | "HumanEdited"; changeSummary: string | null; rejectionReason: string | null; createdAt: string; updatedAt: string; submittedAt: string | null; approvedAt: string | null; approvedByUserId: string | null; scenes: MovieScreenplayScene[] };
export type MovieStoryRevisionSummary = Pick<MovieStoryRevision, "id" | "revisionNumber" | "status" | "authorship" | "changeSummary" | "createdAt" | "submittedAt" | "approvedAt">;
export type MovieStory = { id: string; movieProjectId: string; workspaceId: string; premise: string; logline: string; synopsis: string; treatment: string; approvalState: "Draft" | "InReview" | "Approved"; currentRevisionId: string | null; approvedRevisionId: string | null; createdAt: string; updatedAt: string; currentRevision: MovieStoryRevision | null; approvedRevision: MovieStoryRevision | null; revisions: MovieStoryRevisionSummary[]; canEdit: boolean; canApprove: boolean };
export type MovieStoryRevisionInput = { premise: string; logline: string; synopsis: string; treatment: string; authorship: MovieStoryRevision["authorship"]; parentRevisionId?: string | null; changeSummary?: string | null; scenes: Array<{ sceneIdentifier: string; actNumber?: number | null; sequenceNumber?: number | null; movieSceneId?: string | null; slugline: string; synopsis?: string | null; elements: Array<{ elementType: MovieScreenplayElementType; content: string; characterName?: string | null; parenthetical?: string | null }> }> };
export type DirectorStoryFieldChange = { field: string; target: string | null; existingContent: string; proposedContent: string };
export type DirectorStoryEvidence = { source: string; sourceType: string | null; sourceId: string | null; revision: string | null; excerpt: string };
export type DirectorStoryFinding = { findingType: "hard_continuity_conflict" | "possible_inconsistency" | "creative_suggestion"; severity: "error" | "warning" | "suggestion" | "info"; category: string; evidence: DirectorStoryEvidence[]; affectedTarget: { targetType: string; targetId: string | null; label: string | null }; explanation: string; suggestedCorrection: string; confidence: number; uncertainty: string | null };
export type MovieProductionContinuityReview = { movieProjectId: string; sceneId: string | null; shotId: string | null; reviewOnly: boolean; assembledAt: string; findings: DirectorStoryFinding[] };
export type DirectorStoryReview = { action: string; baseRevisionId: string | null; changes: DirectorStoryFieldChange[]; findings: DirectorStoryFinding[]; appliesToStory: boolean };
export type DirectorAction = { id: string; proposalId: string; actionType: string; status: string; approvalRequired: boolean; failureCode: string | null; createdAt: string; approvedAt: string | null; startedAt: string | null; completedAt: string | null; results: { id: string; status: string; safeMessage: string; resultJson: string | null; createdAt: string }[] };
export type DirectorShotProposal = { shotNumber: number; narrativePurpose: string; estimatedDurationSeconds: number; shotSize: string; framing: string; cameraAngle: string; cameraMovement: string; subject: string; characterAction: string; expressionEmotionalState: string; environment: string; importantProps: string[]; composition: string; lightingIntent: string; depthBackgroundIntent: string; transitionRelationship: string; dialogueAudioDependency: string; continuityRequirements: string; vfxRequirements: string; productionNotes: string; groundingEvidence: string[] };
export type DirectorShotPlanReview = { sceneId: string; sceneTitle: string; contextHash: string; sceneDurationSeconds: number | null; existingActiveDurationSeconds: number; proposedDurationSeconds: number; runtimeCompatible: boolean; isRegeneration: boolean };
export type DirectorAudioBridgeRecommendation = { recommendationId: string; timelineId: string; timelineVersion: number; kind: "sfx" | "ambience" | "music"; layer: string; fromClipId: string | null; toClipId: string | null; relatedTransitionId: string | null; startSeconds: number; durationSeconds: number; suggestedSourceId: string | null; suggestedSourceKind: string | null; rationale: string; requiresUserOverride: boolean };
export type DirectorEditRepairAudioReview = { action: string; timelineId: string; timelineVersion: number; canonicalTimelineFingerprint: string; recommendations: DirectorAudioBridgeRecommendation[]; unresolvedNeeds: string[]; requiresUserOverride: boolean; providerCalled: boolean; canonicalTimelineMutated: boolean };
export type MovieIntercutCoverageRequest = { clipId: string; movieShotId: string; movieSceneId: string; sceneSequence: number; shotSequence: number; durationSeconds: number; coverageKind: "primary" | "reaction" | "insert"; anchorShotId?: string | null; label?: string | null };
export type MovieCanonicalTimelineClip = { clipId: string; movieShotId: string; sequence: number; startSeconds: number; durationSeconds: number };
export type MovieCanonicalTimelineTransition = { id: string; type: string; fromClipId: string | null; toClipId: string | null; startSeconds: number; durationSeconds: number };
export type MovieCanonicalTimeline = { timelineId: string; version: number; clips: MovieCanonicalTimelineClip[]; transitions: MovieCanonicalTimelineTransition[]; contractVersion: string };
export type MovieTimelineEditDecision = { decisionId: string; timelineId: string; baseTimelineVersion: number; action: "add" | "update" | "remove"; transitionId: string | null; proposedTransition: MovieCanonicalTimelineTransition | null; directorRecommendation?: unknown | null; userOverride?: unknown | null };
export type MovieTimelineTransitionEdit = { id: string; movieProjectId: string; baseTimelineVersion: number; resultTimelineVersion: number; action: string; timeline: MovieCanonicalTimeline; createdAt: string };
export type MovieIntercutPlan = { proposalId: string; movieProjectId: string; timelineId: string; baseTimelineVersion: number; proposedTimelineVersion: number; proposedTimeline: MovieCanonicalTimeline; coverage: MovieIntercutCoverageRequest[]; requestedClipOrder: string[]; storyOrderPreserved: boolean; requiresUserApproval: boolean; findings: string[]; provenanceHash: string };
export type DirectorProposal = { id: string; movieProjectId: string; status: string; title: string; summary: string; rationale: string[]; plan: DirectorPlanItem[]; actions: DirectorAction[]; createdAt: string; approvedAt: string | null; storyReview: DirectorStoryReview | null; shotPlan: DirectorShotProposal[] | null; shotPlanReview: DirectorShotPlanReview | null; editRepairAudio: DirectorEditRepairAudioReview | null };
export type DirectorStoryBoundedContext = { movieProjectId: string; workspaceId: string; movieBrief: string; guide: DirectorGuideContext; currentRevision: unknown | null; approvedRevision: unknown | null; targetScene: unknown | null; relevantMovieScenes: unknown[]; relevantCharacters: unknown[]; relevantWorldReferences: unknown[]; assembledAt: string; contextVersion: number; durationSeconds: number; language: string };
export type DirectorProposalResponse = { proposal: DirectorProposal; context: unknown; storyContext: DirectorStoryBoundedContext | null };
export type DirectorStoryAction = "develop_premise" | "improve_logline" | "expand_synopsis" | "create_refine_treatment" | "propose_screenplay_scene" | "rewrite_selected_passage" | "improve_dialogue" | "tighten_pacing" | "identify_story_inconsistencies";
export type MovieSceneWorldReference = { id: string; name: string; role: string | null };
export type MovieSceneWorkspace = { id: string; sequence: number; title: string; slug: string; description: string; purpose: string | null; durationSeconds: number | null; productionStatus: string; approvalState: string; storyPosition: string; actSequence: number; sequencePosition: number; movieSequenceId: string | null; screenplaySceneId: string | null; screenplaySceneIdentifier: string | null; screenplaySource: string | null; screenplaySynopsis: string | null; screenplayRevisionNumber: number | null; characters: string[]; locations: MovieSceneWorldReference[]; sets: MovieSceneWorldReference[]; continuityWarnings: string[]; shotCount: number; archivedAt: string | null; createdAt: string; updatedAt: string };
export type MovieScenesSequence = { id: string; sequence: number; title: string; summary: string | null; status: string; scenes: MovieSceneWorkspace[] };
export type MovieScenesAct = { id: string; sequence: number; title: string; summary: string | null; status: string; sequences: MovieScenesSequence[] };
export type MovieScenesWorkspace = { movieProjectId: string; projectTitle: string; productionStatus: string; screenplayApprovalState: string; approvedRevisionId: string | null; approvedRevisionNumber: number | null; acts: MovieScenesAct[]; sceneCount: number; linkedSceneCount: number; shotCount: number };
export type MovieScenesBreakdown = { movieProjectId: string; approvedRevisionId: string; approvedRevisionNumber: number; createdSceneCount: number; existingLinkedSceneCount: number; workspace: MovieScenesWorkspace };
export type MovieWorkspaceScene = Omit<MovieScene, "shots" | "clips"> & { shotCount: number; clips: Pick<MovieClip, "id" | "movieSceneId" | "movieShotId" | "assetId" | "status" | "durationSeconds">[] };
export type MovieWorkspaceCharacter = Pick<MovieCharacter, "id" | "name" | "role" | "description" | "appearance" | "voiceAndPerformance" | "continuityNotes"> & { createdAt: string; updatedAt: string };
export type MovieWorkspaceLocation = Pick<MovieLocation, "id" | "name" | "description" | "visualContinuityNotes">;
export type MovieWorkspaceAssembly = Pick<MovieAssembly, "id" | "assetId" | "status" | "createdAt" | "completedAt">;
export type MovieWorkspaceProject = Omit<MovieProject, "scenes" | "characters" | "locations" | "clips" | "assemblies" | "world" | "guide"> & { guide: Pick<MovieGuide, "id" | "visualLanguage" | "cameraLanguage" | "colorAndLighting" | "soundAndNarration" | "continuityRules" | "updatedAt">; scenes: MovieWorkspaceScene[]; characters: MovieWorkspaceCharacter[]; locations: MovieWorkspaceLocation[]; clips: Pick<MovieClip, "id" | "movieSceneId" | "movieShotId" | "assetId" | "status" | "durationSeconds">[]; assemblies: MovieWorkspaceAssembly[]; world: { locations: MovieWorkspaceLocation[] } | null };
export type MovieWorkspaceResponse = { module: string; project: MovieWorkspaceProject };
export const movieOperationalActions = { storyEdit: "story.edit", storyApproval: "story.approve", guideEdit: "guide.edit", guideApproval: "guide.approve", castEdit: "cast.edit", worldEdit: "world.edit", sceneEdit: "scene.edit", shotEdit: "shot.edit", productionVersionEdit: "production.version.edit", generate: "generate", renderTake: "render.take", storyboardApproval: "storyboard.approve", keyframeApproval: "keyframe.approve", productionReview: "production.review", takeCreate: "take.create", takeSelect: "take.select", takeApproval: "take.approve", takeFinalization: "take.finalize", directorProposalCreate: "director.proposal.create", directorProposalApproval: "director.proposal.approve", directorProposalExecution: "director.proposal.execute", comments: "comments", reviewsRequest: "reviews.request", reviewsDecision: "reviews.decide", finalReviewDecision: "reviews.final.decide", teamManagement: "team.manage", budgetManagement: "budget.manage" } as const;
export type MovieOperationalAction = typeof movieOperationalActions[keyof typeof movieOperationalActions];
export type MovieCapabilityResponse = { movieProjectId: string; permissions: string[]; capabilities: Record<string, boolean> };
export function canMovieAction(capabilities: MovieCapabilityResponse | null | undefined, action: MovieOperationalAction): boolean { return capabilities?.capabilities[action] === true; }
export type MovieProviderReadiness = { ready: boolean; supportedOperations: string[] };
export type MovieStudioResponse = { project: MovieProject; job: GenerationJob | null };
export type MovieStudioCreateInput = { workspaceId: string; projectId?: string | null; mode: "Quick" | "Full"; title: string; description: string; durationSeconds: number; aspectRatio: string; style: string; language: string; additionalInstructions?: string | null; visualLanguage?: string | null; cameraLanguage?: string | null; colorAndLighting?: string | null; soundAndNarration?: string | null; continuityRules?: string | null; cinematography?: CinematographyIntentSelection | null };
export type DirectorQualityLevel = "Fast" | "Standard" | "Cinematic" | "Studio";
export type DirectorSelectionMode = DirectorQualityLevel | "Auto";
export type DirectorQualityRecommendation = { qualityLevel: DirectorQualityLevel; selectionMode: DirectorSelectionMode; importanceScore: number; complexityScore: number; budgetSensitivityScore: number; estimatedCostUsd: number | null; reasons: string[]; budgetConstrained: boolean };
export type DirectorPlanItem = { shotId: string; sequence: number; description: string; recommendation: DirectorQualityRecommendation };
export type DirectorActionResult = { id: string; status: string; safeMessage: string; resultJson: string | null; createdAt: string };
export type DirectorGuideContext = { visualLanguage: string; cameraLanguage: string; colorAndLighting: string; soundAndNarration: string; continuityRules: string; revisionNumber: number | null; isAuthoritative: boolean; cinematographyBibleJson: string | null };
export type DirectorRoomContext = { room: string; selectedSceneId: string | null; selectedShotId: string | null; availablePrerequisites: { key: string; label: string; satisfied: boolean; detail: string }[]; validActions: string[]; appropriateTarget: { type: string; id: string; storyRevisionId: string | null; sceneId: string | null; shotId: string | null; productionVersionId: string | null; takeId: string | null } | null };
export type DirectorContext = { movieProjectId: string; workspaceId: string; title: string; description: string; durationSeconds: number; aspectRatio: string; style: string; language: string; guide: DirectorGuideContext; scenes: { id: string; sequence: number; title: string; summary: string; durationSeconds: number | null; continuityNotes: string | null; shots: { id: string; sequence: number; description: string; cameraAndFraming: string | null; cameraMotion: string | null; durationSeconds: number | null; narration: string | null; dialogue: string | null; visualContinuityNotes: string | null }[] }[]; characters: { name: string; description: string; appearance: string | null; continuityNotes: string | null }[]; locations: { name: string; description: string; visualContinuityNotes: string | null }[]; assembledAt: string; contextVersion: number; approvedStory: { revisionId: string; revisionNumber: number; premise: string; logline: string; synopsis: string; treatment: string; authorship: string } | null; roomContext: DirectorRoomContext | null };
export type DirectorHistoryEvent = { id: string; eventType: string; safeDetailsJson: string | null; createdAt: string };
export type DirectorProposalInput = { shotId?: string | null; planEditRepairAudio?: boolean; editRepairAudioAction?: "plan_audio_bridges"; canonicalTimeline?: unknown; audioCapabilities?: unknown[]; storyAction?: DirectorStoryAction; shotPlanningAction?: "propose_shots" | "regenerate_shots"; requestedShotCount?: number | null; contextRoom?: string | null; roomAction?: string | null; contextTargetType?: string | null; contextTargetId?: string | null; selectedSceneId?: string | null; selectedShotId?: string | null; targetSceneId?: string | null; targetElementId?: string | null; selectedPassage?: string | null; goal?: string | null; requestedQuality?: DirectorSelectionMode; budgetLimitUsd?: number | null; importance?: number; complexity?: number; budgetSensitivity?: number };
export type DirectorActionExecutionResponse = { action: DirectorAction; result: DirectorActionResult };
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
let csrfRequest: Promise<string> | null = null;
let csrfRefreshRequest: Promise<string> | null = null;

function requestId() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

function generationInit(init: RequestInit, idempotencyKey: string): RequestInit {
  const headers = new Headers(init.headers);
  headers.set("Idempotency-Key", idempotencyKey);
  return { ...init, headers };
}

async function csrf(forceRefresh = false) {
  if (csrfRefreshRequest) return csrfRefreshRequest;
  if (csrfToken && !forceRefresh) return csrfToken;
  if (csrfRequest && !forceRefresh) return csrfRequest;
  const request = (async () => {
    // A forced refresh follows any in-flight bootstrap, but does not share its
    // token: an auth transition may have changed the antiforgery user binding
    // while the earlier request was still completing.
    if (forceRefresh && csrfRequest) await csrfRequest.catch(() => undefined);
    const response = await fetch(`${API_URL}/api/auth/csrf`, { credentials: "include", cache: "no-store" });
    if (!response.ok) throw new Error("CSRF token unavailable");
    const body = await response.json().catch(() => null) as { token?: unknown } | null;
    if (!body || typeof body.token !== "string" || body.token.length === 0) throw new Error("CSRF token unavailable");
    csrfToken = body.token;
    return body.token;
  })();
  if (forceRefresh) csrfRefreshRequest = request;
  else csrfRequest = request;
  try {
    return await request;
  } finally {
    if (forceRefresh && csrfRefreshRequest === request) csrfRefreshRequest = null;
    if (!forceRefresh && csrfRequest === request) csrfRequest = null;
  }
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
  const streamController = new AbortController();
  const streamSignal = signal ? AbortSignal.any([signal, streamController.signal]) : streamController.signal;
  let response: Response;
  try {
    response = await awaitWithChatStreamWatchdog(
      fetch(`${API_URL}${path}`, { method: "POST", headers, credentials: "include", body: JSON.stringify(payload), signal: streamSignal }),
      signal,
      CHAT_STREAM_WATCHDOG_TIMEOUT_MS,
      () => streamController.abort(),
    );
  } catch (error) {
    if (error instanceof ChatStreamTransportError) {
      throw new ApiError(error.code === "STREAM_TIMEOUT" ? 504 : 502, error.message, undefined, error.code);
    }
    throw error;
  }
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
  let cancelReader = false;
  try {
    while (true) {
      let result: ReadableStreamReadResult<Uint8Array>;
      try {
        result = await awaitWithChatStreamWatchdog(
          reader.read(),
          signal,
          CHAT_STREAM_WATCHDOG_TIMEOUT_MS,
          () => {
            cancelReader = true;
            streamController.abort();
          },
        );
      } catch (error) {
        if (error instanceof ChatStreamTransportError) {
          cancelReader = true;
          throw new ApiError(error.code === "STREAM_TIMEOUT" ? 504 : 502, error.message, undefined, error.code);
        }
        throw error;
      }
      parser.push(decoder.decode(result.value ?? new Uint8Array(), { stream: !result.done }));
      if (result.done) break;
    }
    parser.push(decoder.decode());
    parser.end();
  } catch (error) {
    // The server has already durably settled the message. A broken connection
    // after the terminal event must not turn a completed response into a
    // duplicate-retry prompt in the caller.
    if (parser.hasTerminalEvent()) {
      cancelReader = true;
      return;
    }
    throw error;
  } finally {
    if (cancelReader) {
      try { await reader.cancel(); } catch { /* the transport may already be closed */ }
    }
    reader.releaseLock();
  }
}

export class ApiError extends Error {
  constructor(public status: number, message: string, public fields?: Record<string, string[]>, public code?: string) { super(message); }
}

const movieWorkspaceRequests = new Map<string, Promise<MovieProject>>;
function normalizeMovieWorkspaceProject(project: MovieWorkspaceProject): MovieProject { return { ...project, guide: { ...project.guide }, scenes: project.scenes.map((scene) => ({ ...scene, shots: [], clips: scene.clips.map((clip) => ({ ...clip, generationJobId: null, metadataJson: null, continuitySnapshotJson: null })) })), characters: project.characters.map((character) => ({ ...character, physicalDescription: null, wardrobe: null, voiceReference: null, personalityAndStoryNotes: null, referenceAssetId: null, referenceAssetIds: [], states: [], relationships: [], continuityLocks: [] })), locations: project.locations.map((location) => ({ ...location, referenceAssetId: null })), clips: project.clips.map((clip) => ({ ...clip, generationJobId: null, metadataJson: null, continuitySnapshotJson: null })), assemblies: project.assemblies.map((assembly) => ({ ...assembly, generationJobId: null, outputFormat: "mp4", metadataJson: null })), world: { locations: (project.world?.locations ?? project.locations).map((location) => ({ ...location, referenceAssetId: null })), sets: [], props: [], references: [], usages: [], facts: [], locks: [] } }; }
function clearMovieWorkspaceRequests() { movieWorkspaceRequests.clear(); }

export type MovieFinalAssembly = {
  id: string;
  movieProjectId: string;
  generationJobId: string | null;
  outputAssetId: string | null;
  status: string;
  resolutionProfile: string;
  outputWidth: number;
  outputHeight: number;
  timelineItemCount: number;
  sourceTakeIds: string[];
  audioMixInputCount: number;
  captionsMode: string;
  qcStatus: string;
  qcResultJson: string | null;
  provenanceJson: string | null;
  progressPercent: number;
  attemptCount: number;
  canResume: boolean;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
  job: GenerationJob | null;
};
export type MovieSoundApproval = { id: string; reviewerUserId: string; decision: string; comment: string | null; createdAt: string };
export type MovieSoundTrack = {
  id: string;
  movieProjectId: string;
  movieSceneId: string | null;
  movieShotId: string | null;
  assetId: string | null;
  generationJobId: string | null;
  libraryReferenceId: string | null;
  kind: string;
  layer: string;
  name: string;
  description: string;
  startMilliseconds: number;
  endMilliseconds: number;
  fadeInMilliseconds: number;
  fadeOutMilliseconds: number;
  gainDb: number;
  status: string;
  sourceKind: string;
  createdAt: string;
  updatedAt: string;
  approvedAt: string | null;
  approvals: MovieSoundApproval[];
};
export type MovieSoundTrackList = { targetId: string; targetType: string; tracks: MovieSoundTrack[] };
export type MovieSoundLibraryReference = {
  id: string;
  movieProjectId: string;
  assetId: string;
  label: string;
  assetName: string;
  mimeType: string | null;
  canPreview: boolean;
  createdAt: string;
  updatedAt: string;
};
export type MovieSoundLibrary = { movieProjectId: string; references: MovieSoundLibraryReference[] };
export type MovieSoundTrackInput = {
  kind: string;
  layer: string;
  name: string;
  description: string;
  startMilliseconds: number;
  endMilliseconds: number;
  fadeInMilliseconds?: number;
  fadeOutMilliseconds?: number;
  gainDb?: number;
  assetId?: string | null;
  libraryReferenceId?: string | null;
  generate?: boolean;
  additionalInstructions?: string | null;
};
export type MovieSoundtrackDuckingIntent = {
  id: string;
  targetLane: string;
  startOffsetSeconds: number;
  endOffsetSeconds: number;
  duckDecibels: number;
  attackMilliseconds: number;
  releaseMilliseconds: number;
  rationale: string | null;
};
export type MovieSoundtrackDuckingIntentInput = {
  targetLane: string;
  startOffsetSeconds: number;
  endOffsetSeconds: number;
  duckDecibels: number;
  attackMilliseconds?: number;
  releaseMilliseconds?: number;
  rationale?: string | null;
};
export type MovieSoundtrackCueVersion = {
  id: string;
  versionNumber: number;
  label: string;
  arrangementIntent: string | null;
  mood: string;
  intensity: number;
  assetId: string | null;
  approvalState: string;
  reviewNote: string | null;
  createdByUserId: string;
  reviewedByUserId: string | null;
  reviewedAt: string | null;
  createdAt: string;
  updatedAt: string;
  audioAssetProvenance: { assetId: string; storedFileId: string; assetType: string; mimeType: string; sizeBytes: number } | null;
  reviews: { id: string; decision: string }[];
};
export type MovieSoundtrackCue = {
  id: string;
  movieProjectId: string;
  movieActId: string;
  movieSceneId: string;
  sequence: number;
  title: string;
  narrativeIntent: string | null;
  mood: string;
  intensity: number;
  actStartSeconds: number;
  sceneStartSeconds: number;
  timelineStartSeconds: number;
  durationSeconds: number;
  approvalState: string;
  approvedVersionId: string | null;
  createdAt: string;
  updatedAt: string;
  duckingIntents: MovieSoundtrackDuckingIntent[];
  versions: MovieSoundtrackCueVersion[];
};
export type MovieSoundtrackCueInput = {
  movieSceneId: string;
  movieActId?: string | null;
  title: string;
  narrativeIntent?: string | null;
  mood: string;
  intensity: number;
  actStartSeconds: number;
  sceneStartSeconds: number;
  timelineStartSeconds: number;
  durationSeconds: number;
  duckingIntents?: MovieSoundtrackDuckingIntentInput[] | null;
};
export type MovieSoundtrack = { movieProjectId: string; mediaServiceAvailable: boolean; cues: MovieSoundtrackCue[] };
export type MovieCaptionCue = {
  id: string;
  movieCaptionTrackId: string;
  sequence: number;
  startTimecode: string;
  endTimecode: string;
  startMilliseconds: number;
  endMilliseconds: number;
  text: string;
  speakerCharacterId: string | null;
  speakerName: string | null;
  movieSceneId: string | null;
  movieShotId: string | null;
  movieTakeId: string | null;
  createdAt: string;
  updatedAt: string;
};
export type MovieCaptionTrack = {
  id: string;
  movieProjectId: string;
  movieAssemblyId: string | null;
  sequence: number;
  name: string;
  trackType: string;
  language: string;
  isRtl: boolean;
  isDefault: boolean;
  status: string;
  sourceFormat: string | null;
  sourceFileName: string | null;
  createdAt: string;
  updatedAt: string;
  cues: MovieCaptionCue[];
};
export type MovieCaptionTimeline = {
  movieProjectId: string;
  trackCount: number;
  cues: { trackId: string; trackName: string; language: string; isRtl: boolean; trackType: string; cue: MovieCaptionCue }[];
};
export type MovieDialogueTake = {
  id: string;
  movieDialogueLineId: string;
  versionNumber: number;
  label: string;
  status: string;
  generationJobId: string | null;
  assetId: string | null;
  storedFileId: string | null;
  durationMilliseconds: number | null;
  metadataJson: string | null;
  createdAt: string;
  updatedAt: string;
  approvedAt: string | null;
  selectedAt: string | null;
  approvals: { id: string; userId: string; decision: string; comment: string | null; createdAt: string }[];
};
export type MovieDialogueLine = {
  id: string;
  movieClipId: string;
  movieCharacterId: string | null;
  selectedTakeId: string | null;
  sequence: number;
  speakerName: string;
  language: string;
  text: string;
  startMilliseconds: number;
  endMilliseconds: number;
  deliveryNotes: string | null;
  status: string;
  createdAt: string;
  updatedAt: string;
  takes: MovieDialogueTake[];
};
export type MovieDialogueClip = { movieClipId: string; movieProjectId: string; lines: MovieDialogueLine[] };
export type MovieTakeSelectRecord = {
  id: string;
  movieTakeId: string;
  selectNumber: number;
  label: string;
  status: string;
  startMilliseconds: number;
  endMilliseconds: number;
  durationMilliseconds: number;
  notes: string | null;
  provenanceJson: string;
  createdByUserId: string;
  createdAt: string;
  updatedAt: string;
  reviewedByUserId: string | null;
  reviewedAt: string | null;
  reviewNote: string | null;
};
export type MovieTeamMember = { id: string; userId: string; displayName: string; role: string; isProjectOwner: boolean; permissions: string[]; createdAt: string; updatedAt: string };
export type MovieComment = { id: string; targetType: string; targetId: string; authorUserId: string; authorDisplayName: string; body: string; parentCommentId: string | null; mentions: { userId: string; displayName: string }[]; createdAt: string; updatedAt: string; resolvedAt: string | null };
export type MovieReview = { id: string; targetType: string; targetId: string; requestedByUserId: string; requestedByDisplayName: string; reviewerUserId: string; reviewerDisplayName: string; isFinal: boolean; status: string; requestNote: string | null; decisionNote: string | null; createdAt: string; reviewedAt: string | null };
export type MovieAssignment = { id: string; targetType: string; targetId: string; assigneeUserId: string; assigneeDisplayName: string; assignedByUserId: string; assignedByDisplayName: string; title: string; description: string | null; status: string; dueAt: string | null; createdAt: string; updatedAt: string; completedAt: string | null };
export type MovieProductionCredit = { id: string; userId: string; displayName: string; role: string; creditName: string | null; sortOrder: number; createdAt: string };
export type MovieCollaboration = { movieProjectId: string; team: MovieTeamMember[]; comments: MovieComment[]; reviews: MovieReview[]; assignments: MovieAssignment[]; credits: MovieProductionCredit[]; currentUserPermissions: string[] };

export const api = {
  me: () => request<AuthResponse>("/api/auth/me"),
  passwordPolicy: () => request<PasswordPolicy>("/api/auth/password-policy"),
  register: async (input: RegisterInput) => { const result = await request<AuthResponse>("/api/auth/register", { method: "POST", body: JSON.stringify(input) }, true); csrfToken = null; clearMovieWorkspaceRequests(); await csrf(true); return result; },
  login: async (input: LoginInput) => { const result = await request<AuthResponse>("/api/auth/login", { method: "POST", body: JSON.stringify(input) }, true); csrfToken = null; await csrf(true); return result; },
  logout: async () => { const result = await request<{ success: boolean }>("/api/auth/logout", { method: "POST" }, true); csrfToken = null; await csrf(true); return result; },
  updateProfile: (input: ProfileInput) => request<AuthResponse>("/api/auth/profile", { method: "PATCH", body: JSON.stringify(input) }, true),
  completeOnboarding: (input: OnboardingInput) => request<AuthResponse>("/api/auth/onboarding/complete", { method: "POST", body: JSON.stringify(input) }, true),
  changePassword: (input: ChangePasswordInput) => request<{ success: boolean }>("/api/auth/password", { method: "POST", body: JSON.stringify(input) }, true),
  revokeOtherSessions: async () => { const result = await request<{ success: boolean }>("/api/auth/sessions/revoke", { method: "POST" }, true); csrfToken = null; await csrf(true); return result; },
  listProjects: (workspaceId: string, status: "Active" | "Archived", signal?: AbortSignal) => request<Project[]>(`/api/workspaces/${workspaceId}/projects?status=${status}`, { signal }),
  listWorkspaces: () => request<Workspace[]>('/api/workspaces'),
  getWorkspace: (workspaceId: string) => request<Workspace>(`/api/workspaces/${workspaceId}`),
  createProject: (workspaceId: string, input: ProjectInput) => request<Project>(`/api/workspaces/${workspaceId}/projects`, { method: "POST", body: JSON.stringify(input) }, true),
  getProject: (projectId: string) => request<Project>(`/api/projects/${projectId}`),
  getProjectOverview: (projectId: string) => request<ProjectOverview>(`/api/projects/${projectId}/overview`),
  updateProject: (projectId: string, input: ProjectInput) => request<Project>(`/api/projects/${projectId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  archiveProject: (projectId: string) => request<Project>(`/api/projects/${projectId}/archive`, { method: "POST" }, true),
  restoreProject: (projectId: string) => request<Project>(`/api/projects/${projectId}/restore`, { method: "POST" }, true),
  listConversations: (workspaceId: string, status: "Active" | "Archived" = "Active", signal?: AbortSignal) => request<Conversation[]>(`/api/workspaces/${workspaceId}/conversations?status=${status}`, { signal }),
  createConversation: (workspaceId: string, input: { title?: string; projectId?: string } = {}) => request<Conversation>(`/api/workspaces/${workspaceId}/conversations`, { method: "POST", body: JSON.stringify(input) }, true),
  getConversation: (conversationId: string, signal?: AbortSignal) => request<Conversation>(`/api/conversations/${conversationId}`, { signal }),
  getMessages: (conversationId: string, signal?: AbortSignal) => request<ChatMessage[]>(`/api/conversations/${conversationId}/messages`, { signal }),
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
  recoverAdminStuckJob: (jobId: string, reason: string) => request<{ jobId: string; status: GenerationJobStatus; retryCount: number; queuedAt: string; auditAction: string }>(`/api/admin/operations/jobs/${jobId}/recover`, { method: "POST", body: JSON.stringify({ reason }) }, true),
  createGenerationJob: (workspaceId: string, inputJson = "{}", title?: string, idempotencyKey = requestId()) => request<GenerationJob>("/api/generation/jobs", generationInit({ method: "POST", body: JSON.stringify({ workspaceId, jobType: "system.test", inputJson, title }) }, idempotencyKey), true),
  createImageGenerationJob: (input: ImageGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/image-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createVoiceGenerationJob: (input: VoiceGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/voice-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createDocumentGenerationJob: (input: DocumentGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/document-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createPresentationGenerationJob: (input: PresentationGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/presentation-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createResearchGenerationJob: (input: ResearchGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/research-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  createSocialGenerationJob: (input: SocialGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/social-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  getMovieProvider: () => request<{ provider: MovieProviderReadiness }>("/api/movie-studio/provider"),
  getCinematographyPresets: () => request<CinematographyPreset[]>("/api/movie-studio/cinematography/presets"),
  getCinematographyPlanningValues: () => request<{ shotSizes: string[]; framings: string[]; cameraAngles: string[]; cameraPositions: string[]; cameraMovements: string[]; compositionIntents: string[]; lensLookIntents: string[]; focalLengthIntents: string[]; depths: string[]; focusIntents: string[]; lightingIntents: string[]; subjectEmphases: string[]; visualTransitionIntents: string[]; exposureLooks: string[] }>("/api/movie-studio/cinematography/planning-values"),
  planMovieCinematography: (shotId: string, input: { characterEmotionalPurpose?: string | null; creativeNotes?: string | null; overrides?: CinematographyShotPlan | null; cameraProfile?: CinematographyCameraProfileOverride | null }) => request<MovieCinematographyPlanResponse>(`/api/movie-studio/shots/${shotId}/cinematography/plan`, { method: "POST", body: JSON.stringify(input) }, true),
  planMovieCameraProfile: (shotId: string, input: { characterEmotionalPurpose?: string | null; creativeNotes?: string | null; overrides?: CinematographyShotPlan | null; cameraProfile?: CinematographyCameraProfileOverride | null }) => request<MovieCinematographyPlanResponse>(`/api/movie-studio/shots/${shotId}/camera-profile`, { method: "POST", body: JSON.stringify(input) }, true),
  createMovieProject: (input: MovieStudioCreateInput) => request<MovieStudioResponse>("/api/movie-studio/projects", { method: "POST", body: JSON.stringify(input) }, true),
  getMovieOverview: (id: string) => request<MovieOverview>(`/api/movie-studio/projects/${id}/overview`),
  getMovieBudgetDirector: (id: string) => request<MovieBudgetDirectorEstimate>(`/api/movie-studio/projects/${id}/budget-director`),
  previewMovieBudgetDirector: (id: string, input: MovieBudgetDirectorRequest) => request<MovieBudgetDirectorEstimate>(`/api/movie-studio/projects/${id}/budget-director/preview`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieProject: (id: string) => request<MovieProject>(`/api/movie-studio/projects/${id}`),
  getMovieTimeline: (id: string) => request<MovieTimeline>(`/api/movie-studio/projects/${id}/timeline`),
  getMovieTimelineTransitions: (id: string) => request<MovieTimelineTransitionEdit>(`/api/movie-studio/projects/${id}/timeline/transition-edits`),
  applyMovieTimelineTransition: (id: string, input: { timeline: MovieCanonicalTimeline; decision: MovieTimelineEditDecision }) => request<MovieTimelineTransitionEdit>(`/api/movie-studio/projects/${id}/timeline/transition-edits`, { method: "POST", body: JSON.stringify(input) }, true),
  createMovieTimelineRevision: (id: string, input: MovieTimelineRevisionRequest) => request<MovieTimelineRevision>(`/api/movie-studio/projects/${id}/timeline/revisions`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieCapabilities: (id: string) => request<MovieCapabilityResponse>(`/api/movie-studio/projects/${id}/capabilities`),
  getMovieWorkspace: (id: string, module = "overview") => { const key = `${id}:${module}`; const pending = movieWorkspaceRequests.get(key); if (pending) return pending; const promise = request<MovieWorkspaceResponse>(`/api/movie-studio/projects/${id}/workspace?module=${encodeURIComponent(module)}`).then((response) => normalizeMovieWorkspaceProject(response.project)).finally(() => movieWorkspaceRequests.delete(key)); movieWorkspaceRequests.set(key, promise); return promise; },
  getMovieWorld: (id: string) => request<MovieWorldWorkspace>(`/api/movie-studio/projects/${id}/world`),
  getMovieCast: (id: string) => request<MovieCast>(`/api/movie-studio/projects/${id}/cast`),
  getMovieCastFromStory: (id: string) => request<MovieStoryCastSuggestions>(`/api/movie-studio/projects/${id}/cast/from-story`),
  getMovieCharacterDetail: (characterId: string) => request<MovieCharacterDetail>(`/api/movie-studio/characters/${characterId}/detail`),
  getMovieCharacterProductionSheet: (characterId: string) => request<MovieCharacterProductionSheet>(`/api/movie-studio/characters/${characterId}/production-sheet`),
  getMovieProjectShell: (id: string) => request<MovieProjectShell>(`/api/movie-studio/projects/${id}/shell`),
  getMovieStory: (id: string) => request<MovieStory>(`/api/movie-studio/projects/${id}/story`),
  listMovieStoryRevisions: (id: string) => request<MovieStoryRevisionSummary[]>(`/api/movie-studio/projects/${id}/story/revisions`),
  getMovieStoryRevision: (projectId: string, revisionId: string) => request<MovieStoryRevision>(`/api/movie-studio/projects/${projectId}/story/revisions/${revisionId}`),
  createMovieStoryRevision: (id: string, input: MovieStoryRevisionInput) => request<MovieStory>(`/api/movie-studio/projects/${id}/story/revisions`, { method: "POST", body: JSON.stringify(input) }, true),
  updateMovieStoryDraft: (projectId: string, revisionId: string, input: MovieStoryRevisionInput) => request<MovieStoryRevision>(`/api/movie-studio/projects/${projectId}/story/revisions/${revisionId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  submitMovieStoryRevision: (projectId: string, revisionId: string) => request<MovieStoryRevision>(`/api/movie-studio/projects/${projectId}/story/revisions/${revisionId}/submit`, { method: "POST" }, true),
  approveMovieStoryRevision: (projectId: string, revisionId: string) => request<MovieStoryRevision>(`/api/movie-studio/projects/${projectId}/story/revisions/${revisionId}/approve`, { method: "POST" }, true),
  rejectMovieStoryRevision: (projectId: string, revisionId: string, reason: string) => request<MovieStoryRevision>(`/api/movie-studio/projects/${projectId}/story/revisions/${revisionId}/reject`, { method: "POST", body: JSON.stringify({ reason }) }, true),
  getMovieScenesWorkspace: (id: string) => request<MovieScenesWorkspace>(`/api/movie-studio/projects/${id}/scenes/workspace`),
  breakDownMovieScreenplay: (id: string) => request<MovieScenesBreakdown>(`/api/movie-studio/projects/${id}/scenes/breakdown`, { method: "POST" }, true),
  addMovieV2Act: (id: string, input: { title: string; summary?: string | null }) => request<{ id: string; sequence: number; title: string }>(`/api/movie-studio/projects/${id}/acts`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieV2Sequence: (actId: string, input: { title: string; summary?: string | null }) => request<{ id: string; sequence: number; title: string }>(`/api/movie-studio/acts/${actId}/sequences`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieV2Scene: (sequenceId: string, input: { title: string; summary: string }) => request<{ id: string; sequence: number; title: string }>(`/api/movie-studio/sequences/${sequenceId}/scenes`, { method: "POST", body: JSON.stringify(input) }, true),
  reorderMovieEntity: (entity: "acts" | "sequences" | "scenes" | "shots" | "takes", id: string, sequence: number) => request<void>(`/api/movie-studio/${entity}/${id}/order`, { method: "PATCH", body: JSON.stringify({ sequence }) }, true),
  updateMovieGuide: (id: string, input: Partial<Omit<MovieGuide, "cinematographyBible">> & { cinematography?: CinematographyIntentSelection | null }) => request<MovieProject>(`/api/movie-studio/projects/${id}/guide`, { method: "PATCH", body: JSON.stringify(input) }, true),
  addMovieScene: (id: string, input: { title: string; summary: string; durationSeconds?: number | null; continuityNotes?: string | null; narration?: string | null; dialogue?: string | null }) => request<MovieScene>(`/api/movie-studio/projects/${id}/scenes`, { method: "POST", body: JSON.stringify(input) }, true),
  updateMovieScene: (sceneId: string, input: { title: string; summary: string; durationSeconds?: number | null; continuityNotes?: string | null; narration?: string | null; dialogue?: string | null }) => request<MovieScene>(`/api/movie-studio/scenes/${sceneId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  addMovieCharacter: (id: string, input: MovieCharacterInput) => request<MovieCharacter>(`/api/movie-studio/projects/${id}/characters`, { method: "POST", body: JSON.stringify(input) }, true),
  updateMovieCharacter: (characterId: string, input: MovieCharacterInput) => request<MovieCharacter>(`/api/movie-studio/characters/${characterId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  addMovieCharacterState: (characterId: string, input: Omit<MovieCharacterState, "id" | "createdAt" | "updatedAt">) => request<MovieCharacterState>(`/api/movie-studio/characters/${characterId}/states`, { method: "POST", body: JSON.stringify(input) }, true),
  updateMovieCharacterState: (stateId: string, input: Omit<MovieCharacterState, "id" | "key" | "createdAt" | "updatedAt"> & { key?: string }) => request<MovieCharacterState>(`/api/movie-studio/character-states/${stateId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  addMovieCharacterRelationship: (characterId: string, input: { relatedCharacterId: string; relationshipType: string; notes?: string | null }) => request<MovieCharacterRelationship>(`/api/movie-studio/characters/${characterId}/relationships`, { method: "POST", body: JSON.stringify(input) }, true),
  lockMovieCharacterFact: (characterId: string, input: { fieldKey: string; lockedValue: string; characterStateId?: string | null }) => request<MovieCharacterContinuityLock>(`/api/movie-studio/characters/${characterId}/continuity-locks`, { method: "POST", body: JSON.stringify(input) }, true),
  saveMovieCharacterProductionSheet: (characterId: string, input: MovieCharacterProductionSheetInput) => request<MovieCharacterProductionSheet>(`/api/movie-studio/characters/${characterId}/production-sheet`, { method: "PUT", body: JSON.stringify(input) }, true),
  approveMovieCharacterProductionSheet: (sheetId: string) => request<MovieCharacterProductionSheet>(`/api/movie-studio/production-sheets/${sheetId}/approve`, { method: "POST" }, true),
  lockMovieCharacterProductionSheet: (sheetId: string) => request<MovieCharacterProductionSheet>(`/api/movie-studio/production-sheets/${sheetId}/lock`, { method: "POST" }, true),
  unlockMovieCharacterProductionSheet: (sheetId: string) => request<MovieCharacterProductionSheet>(`/api/movie-studio/production-sheets/${sheetId}/unlock`, { method: "POST" }, true),
  addMovieContinuityLock: (id: string, input: { entityType: string; entityId?: string | null; fieldName: string; lockedValue: string; strength?: string | null; reason?: string | null }) => request<MovieContinuityLock>(`/api/movie-studio/projects/${id}/continuity-locks`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieShot: (sceneId: string, input: MovieShotPlanningInput) => request<MovieShot>(`/api/movie-studio/scenes/${sceneId}/shots`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieSceneShotPlan: (sceneId: string) => request<MovieSceneShotPlan>(`/api/movie-studio/scenes/${sceneId}/shots`),
  getMovieShotPlanning: (shotId: string) => request<MovieShot>(`/api/movie-studio/shots/${shotId}`),
  updateMovieShot: (shotId: string, input: MovieShotPlanningInput & { status?: string | null }) => request<MovieShot>(`/api/movie-studio/shots/${shotId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  reorderMovieShot: (shotId: string, sequence: number) => request<MovieSceneShotPlan>(`/api/movie-studio/shots/${shotId}/reorder`, { method: "POST", body: JSON.stringify({ sequence }) }, true),
  archiveMovieShot: (shotId: string) => request<MovieSceneShotPlan>(`/api/movie-studio/shots/${shotId}/archive`, { method: "POST" }, true),
  getMovieStoryboard: (projectId: string) => request<MovieStoryboardProject>(`/api/movie-studio/projects/${projectId}/storyboard`),
  addMovieLocation: (id: string, input: { name: string; description: string; visualContinuityNotes?: string | null; referenceAssetId?: string | null }) => request<MovieLocation>(`/api/movie-studio/projects/${id}/locations`, { method: "POST", body: JSON.stringify(input) }, true),
  updateMovieLocation: (locationId: string, input: { name: string; description: string; visualContinuityNotes?: string | null; referenceAssetId?: string | null }) => request<MovieLocation>(`/api/movie-studio/locations/${locationId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  getMovieLocationGeographySheet: (locationId: string) => request<MovieLocationGeographySheet>(`/api/movie-studio/locations/${locationId}/geography-sheet`),
  upsertMovieLocationGeographySheet: (locationId: string, input: { establishingReferenceAssetId?: string | null; establishingReferenceNotes?: string | null; wideThreeQuarterReferenceAssetId?: string | null; wideThreeQuarterReferenceNotes?: string | null; entrancesExits: MovieLocationGeographyOpening[]; windows: MovieLocationGeographyOpening[]; paths: MovieLocationGeographyPath[]; majorObjects: MovieLocationGeographyEntry[]; lightSources: MovieLocationGeographyEntry[]; orientationAnchors: MovieLocationGeographyEntry[] }) => request<MovieLocationGeographySheet>(`/api/movie-studio/locations/${locationId}/geography-sheet`, { method: "PUT", body: JSON.stringify(input) }, true),
  addMovieLocationGeographyVariant: (locationId: string, input: { name: string; description?: string | null; timeOfDay?: string | null; weather?: string | null; lighting?: string | null; colorPalette?: string | null; referenceAssetId?: string | null; continuityNotes?: string | null }) => request<MovieLocationGeographyVariant>(`/api/movie-studio/locations/${locationId}/geography-sheet/variants`, { method: "POST", body: JSON.stringify(input) }, true),
  approveMovieLocationGeographySheet: (locationId: string) => request<MovieLocationGeographySheet>(`/api/movie-studio/locations/${locationId}/geography-sheet/approve`, { method: "POST", body: "{}" }, true),
  approveMovieLocationGeographyVariant: (variantId: string) => request<MovieLocationGeographyVariant>(`/api/movie-studio/location-geography-variants/${variantId}/approve`, { method: "POST", body: "{}" }, true),
  updateMovieSet: (setId: string, input: { name: string; description: string; environmentType?: string | null; movieLocationId?: string | null; visualDescription?: string | null; timeOfDay?: string | null; weather?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null }) => request<MovieSet>(`/api/movie-studio/sets/${setId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  updateMovieProp: (propId: string, input: { name: string; description: string; category?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null }) => request<MovieProp>(`/api/movie-studio/props/${propId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  addMovieSet: (id: string, input: { name: string; description: string; environmentType?: string | null; movieLocationId?: string | null; visualDescription?: string | null; timeOfDay?: string | null; weather?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null }) => request<MovieSet>(`/api/movie-studio/projects/${id}/sets`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieSetVariation: (setId: string, input: { name: string; visualDescription?: string | null; timeOfDay?: string | null; weather?: string | null; lighting?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null; isDefault?: boolean }) => request<MovieSetVariation>(`/api/movie-studio/sets/${setId}/variations`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieProp: (id: string, input: { name: string; description: string; category?: string | null; continuityNotes?: string | null; referenceAssetId?: string | null }) => request<MovieProp>(`/api/movie-studio/projects/${id}/props`, { method: "POST", body: JSON.stringify(input) }, true),
  getMoviePropBible: (id: string) => request<MoviePropBibleCollection>(`/api/movie-studio/projects/${id}/prop-bible`),
  detectRecurringMovieProps: (id: string) => request<MovieRecurringPropDetection>(`/api/movie-studio/projects/${id}/prop-bible/recurring-detection`),
  getMoviePropProductionSheet: (propId: string) => request<MoviePropBible>(`/api/movie-studio/props/${propId}/production-sheet`),
  saveMoviePropProductionSheet: (propId: string, input: { identityKey?: string | null; role?: string | null; visualIdentity?: string | null; continuityRules?: string | null }) => request<MoviePropBible>(`/api/movie-studio/props/${propId}/production-sheet`, { method: "PUT", body: JSON.stringify(input) }, true),
  addMoviePropBibleReference: (propId: string, input: { assetId: string; role?: string | null; notes?: string | null }) => request<MoviePropBible>(`/api/movie-studio/props/${propId}/production-sheet/references`, { method: "POST", body: JSON.stringify(input) }, true),
  addMoviePropBibleVariant: (propId: string, input: { key: string; label: string; description?: string | null; state?: string | null; visualNotes?: string | null; referenceAssetId?: string | null }) => request<MoviePropBible>(`/api/movie-studio/props/${propId}/production-sheet/variants`, { method: "POST", body: JSON.stringify(input) }, true),
  createMoviePropBibleVersion: (propId: string, input: { identityKey?: string | null; role?: string | null; visualIdentity?: string | null; continuityRules?: string | null }) => request<MoviePropBible>(`/api/movie-studio/props/${propId}/production-sheet/versions`, { method: "POST", body: JSON.stringify(input) }, true),
  reviewMoviePropBibleVersion: (versionId: string, input: { approve: boolean; reason?: string | null }) => request<MoviePropBible>(`/api/movie-studio/prop-bible/versions/${versionId}/review`, { method: "POST", body: JSON.stringify(input) }, true),
  lockMoviePropBibleVersion: (versionId: string) => request<MoviePropBible>(`/api/movie-studio/prop-bible/versions/${versionId}/lock`, { method: "POST" }, true),
  addMovieWorldReference: (id: string, input: { name: string; kind: string; description?: string | null; tagsJson?: string | null; assetId?: string | null }) => request<MovieWorldReference>(`/api/movie-studio/projects/${id}/world-references`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieShotProduction: (shotId: string) => request<MovieShotProduction>(`/api/movie-studio/shots/${shotId}/production`),
  getMovieProductionPreflight: (shotId: string, input: { sourceVersionId?: string | null; targetResolution?: string | null; qualityTier?: string | null } = {}) => request<MovieProductionPreflight>(`/api/movie-studio/shots/${shotId}/production/preflight?${new URLSearchParams(Object.fromEntries(Object.entries({ sourceVersionId: input.sourceVersionId, targetResolution: input.targetResolution, qualityTier: input.qualityTier }).filter((entry): entry is [string, string] => typeof entry[1] === "string" && entry[1].length > 0))).toString()}`),
  reviewMovieProductionContinuity: (projectId: string, sceneId?: string | null, shotId?: string | null) => request<MovieProductionContinuityReview>(`/api/movie-studio/projects/${projectId}/continuity-review${sceneId || shotId ? `?${new URLSearchParams({ ...(sceneId ? { sceneId } : {}), ...(shotId ? { shotId } : {}) }).toString()}` : ""}`),
  reviewMovieSceneContinuity: (sceneId: string) => request<MovieProductionContinuityReview>(`/api/movie-studio/scenes/${sceneId}/continuity-review`),
  reviewMovieShotContinuity: (shotId: string) => request<MovieProductionContinuityReview>(`/api/movie-studio/shots/${shotId}/continuity-review`),
  getMovieProductionCheckpoint: (projectId: string) => request<MovieProductionCheckpoint>(`/api/movie-studio/projects/${projectId}/production/checkpoint`),
  recoverMovieProduction: (projectId: string, generationJobId: string, idempotencyKey = requestId()) => request<MovieProductionRecoveryResponse>(`/api/movie-studio/projects/${projectId}/production/checkpoint/recover`, generationInit({ method: "POST", body: JSON.stringify({ generationJobId }) }, idempotencyKey), true),
  createMovieProductionVersion: (shotId: string, input: MovieProductionVersionInput, idempotencyKey = requestId()) => request<MovieProductionVersion>(`/api/movie-studio/shots/${shotId}/production/versions`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  generateMovieKeyframe: (shotId: string, input: MovieKeyframeGenerationInput, idempotencyKey = requestId()) => request<{ version: MovieProductionVersion; job: GenerationJob }>(`/api/movie-studio/shots/${shotId}/production/keyframe`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  reviewMovieProductionVersion: (versionId: string, input: MovieProductionReviewInput) => request<MovieProductionVersion>(`/api/movie-studio/production/versions/${versionId}/review`, { method: "POST", body: JSON.stringify(input) }, true),
  selectMovieKeyframe: (versionId: string) => request<MovieProductionVersion>(`/api/movie-studio/production/versions/${versionId}/select-keyframe`, { method: "POST" }, true),
  createMovieMotionPreview: (shotId: string, input: { sourceVersionId: string; label?: string | null; compositionJson?: string; stageProvenanceJson?: string | null }) => request<MovieProductionVersion>(`/api/movie-studio/shots/${shotId}/production/motion-preview`, { method: "POST", body: JSON.stringify({ compositionJson: "{}", ...input }) }, true),
  queueMovieProductionRender: (shotId: string, input: { sourceVersionId: string; label?: string | null; title?: string | null; targetResolution?: string | null; qualityTier?: string | null; estimatedProviderCostUsd?: number | null }, idempotencyKey = requestId()) => request<{ version: MovieProductionVersion; job: GenerationJob; clipId: string; project: MovieProject }>(`/api/movie-studio/shots/${shotId}/production/render`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  createMovieTakeFromProduction: (versionId: string, input: { label?: string | null; qualityLevel?: string; notes?: string | null }) => request<MovieTake>(`/api/movie-studio/production/versions/${versionId}/take`, { method: "POST", body: JSON.stringify(input) }, true),
  approveMovieTake: (takeId: string, input: { decision: "Approved" | "Rejected"; comment?: string | null }) => request<MovieTake>(`/api/movie-studio/takes/${takeId}/approvals`, { method: "POST", body: JSON.stringify(input) }, true),
  selectMovieTake: (takeId: string) => request<void>(`/api/movie-studio/takes/${takeId}/select`, { method: "POST" }, true),
  finalizeMovieTake: (takeId: string) => request<void>(`/api/movie-studio/takes/${takeId}/finalize`, { method: "POST" }, true),
  getMovieTakeUpscaleEligibility: (takeId: string, targetMasterResolution = "4k") => request<MovieTakeUpscaleEligibility>(`/api/movie-studio/takes/${takeId}/upscale-eligibility?targetMasterResolution=${encodeURIComponent(targetMasterResolution)}`),
  requestMovieTakeUpscale: (takeId: string, input: { targetMasterResolution?: string; sourceResolution?: string | null } = {}) => request<MovieTakeUpscaleEligibility>(`/api/movie-studio/takes/${takeId}/upscale`, { method: "POST", body: JSON.stringify({ targetMasterResolution: "4k", ...input }) }, true),
  getMovieFinalMaster: (shotId: string) => request<MovieFinalMaster>(`/api/movie-studio/shots/${shotId}/final-mastering`),
  requestMovieFinalMaster: (takeId: string, input: { targetProfile?: string; supersedesMasterId?: string | null } = {}) => request<MovieFinalMaster>(`/api/movie-studio/takes/${takeId}/final-mastering`, { method: "POST", body: JSON.stringify({ targetProfile: "Uhd4K", ...input }) }, true),
  createMovieRegenerationRequest: (shotId: string, input: { actionType: string; requestedStage: MovieProductionStage; reason: string; sourceVersionId?: string | null; changedInputsJson: string; compositionJson?: string }, idempotencyKey = requestId()) => request<MovieSelectiveRegenerationResponse>(`/api/movie-studio/shots/${shotId}/regeneration-requests`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  confirmMovieRegeneration: (regenerationRequestId: string, confirm = true, idempotencyKey = requestId()) => request<MovieSelectiveRegenerationResponse>(`/api/movie-studio/regeneration-requests/${regenerationRequestId}/confirm`, generationInit({ method: "POST", body: JSON.stringify({ confirm }) }, idempotencyKey), true),
  getMovieRegenerationRequest: (requestId: string) => request<MovieSelectiveRegenerationResponse>(`/api/movie-studio/regeneration-requests/${requestId}`),
  generateMovieScene: (projectId: string, sceneId: string, input: { title?: string | null } = {}, idempotencyKey = requestId()) => request<{ project: MovieProject; job: GenerationJob; clipId: string }>(`/api/movie-studio/projects/${projectId}/scenes/${sceneId}/generate`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  generateMovieShot: (shotId: string, input: { title?: string | null } = {}, idempotencyKey = requestId()) => request<{ project: MovieProject; job: GenerationJob; clipId: string }>(`/api/movie-studio/shots/${shotId}/generate`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  lockMovieGuide: (projectId: string, revisionNumber?: number | null) => request<{ revision: { revisionNumber: number; status: string }; authoritativeContext: unknown }>(`/api/movie-studio/projects/${projectId}/guide/lock`, { method: "POST", body: JSON.stringify({ revisionNumber: revisionNumber ?? null }) }, true),
  applyMovieTimelineTransitionEdit: (projectId: string, input: { baseRevisionId?: string | null; decision: unknown }) => request<MovieTimelineRevision>(`/api/movie-studio/projects/${projectId}/timeline/transitions`, { method: "POST", body: JSON.stringify(input) }, true),
  createMovieDirectorProposal: (projectId: string, input: DirectorProposalInput) => request<DirectorProposalResponse>(`/api/movie-director/projects/${projectId}/proposals`, { method: "POST", body: JSON.stringify(input) }, true),
  createMovieIntercutProposal: (projectId: string, input: { timelineId?: string | null; baseTimelineVersion: number; clips: MovieIntercutCoverageRequest[]; requestedClipOrder?: string[] | null }) => request<MovieIntercutPlan>(`/api/movie-director/projects/${projectId}/intercut-proposals`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieDirectorProposal: (proposalId: string) => request<DirectorProposalResponse>(`/api/movie-director/proposals/${proposalId}`),
  getMovieDirectorHistory: (projectId: string) => request<DirectorHistoryEvent[]>(`/api/movie-director/projects/${projectId}/history`),
  approveMovieDirectorProposal: (proposalId: string) => request<DirectorProposal>(`/api/movie-director/proposals/${proposalId}/approve`, { method: "POST", body: JSON.stringify({}) }, true),
  rejectMovieDirectorProposal: (proposalId: string) => request<DirectorProposal>(`/api/movie-director/proposals/${proposalId}/reject`, { method: "POST", body: JSON.stringify({}) }, true),
  executeMovieDirectorAction: (actionId: string) => request<DirectorActionExecutionResponse>(`/api/movie-director/actions/${actionId}/execute`, { method: "POST", body: JSON.stringify({}) }, true),
  createMusicGenerationJob: (input: MusicGenerationInput, idempotencyKey = requestId()) => request<{ job: GenerationJob }>("/api/music-generation/jobs", generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true).then((response) => response.job),
  getGenerationJob: (jobId: string) => request<GenerationJob>(`/api/generation/jobs/${jobId}`),
  getResearchSources: (jobId: string) => request<{ jobId: string; sources: ResearchSource[] }>(`/api/research-generation/jobs/${jobId}/sources`),
  downloadResearchSourcesExport: async (jobId: string) => {
    const response = await fetch(`${API_URL}/api/research-generation/jobs/${jobId}/sources/export`, { credentials: "include" });
    if (!response.ok) throw new ApiError(response.status, "Research source manifest unavailable.", undefined, "RESEARCH_SOURCE_EXPORT_UNAVAILABLE");
    return response.blob();
  },
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
  // Final assembly and master export.
  queueMovieFinalAssembly: (projectId: string, input: { resolutionProfile?: string; includeApprovedSoundtrackCues?: boolean; timeline?: { takeId: string; inPointSeconds?: number; outPointSeconds?: number }[]; captions?: { mode?: string; assetId?: string | null; language?: string } }, idempotencyKey = requestId()) => request<MovieFinalAssembly>(`/api/movie-studio/projects/${projectId}/final-assembly`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  getMovieFinalAssembly: (assemblyId: string) => request<MovieFinalAssembly>(`/api/movie-studio/final-assemblies/${assemblyId}`),
  movieFinalAssemblyDownloadUrl: (assemblyId: string) => `${API_URL}/api/movie-studio/final-assemblies/${assemblyId}/download`,
  // Sound library and per-target sound tracks.
  getMovieSoundLibrary: (projectId: string) => request<MovieSoundLibrary>(`/api/movie-sound/projects/${projectId}/library`),
  addMovieSoundLibraryReference: (projectId: string, input: { assetId: string; label?: string | null }) => request<MovieSoundLibrary>(`/api/movie-sound/projects/${projectId}/library`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieSceneSoundTracks: (sceneId: string) => request<MovieSoundTrackList>(`/api/movie-sound/scenes/${sceneId}/tracks`),
  getMovieShotSoundTracks: (shotId: string) => request<MovieSoundTrackList>(`/api/movie-sound/shots/${shotId}/tracks`),
  createMovieSceneSoundTrack: (sceneId: string, input: MovieSoundTrackInput) => request<MovieSoundTrack>(`/api/movie-sound/scenes/${sceneId}/tracks`, { method: "POST", body: JSON.stringify(input) }, true),
  createMovieShotSoundTrack: (shotId: string, input: MovieSoundTrackInput) => request<MovieSoundTrack>(`/api/movie-sound/shots/${shotId}/tracks`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieSoundTrack: (trackId: string) => request<MovieSoundTrack>(`/api/movie-sound/tracks/${trackId}`),
  reviewMovieSoundTrack: (trackId: string, input: { approve: boolean; comment?: string | null }) => request<MovieSoundTrack>(`/api/movie-sound/tracks/${trackId}/review`, { method: "POST", body: JSON.stringify(input) }, true),
  // Soundtrack cues and versions.
  getMovieSoundtrack: (projectId: string) => request<MovieSoundtrack>(`/api/movie-studio/projects/${projectId}/soundtrack`),
  createMovieSoundtrackCue: (projectId: string, input: MovieSoundtrackCueInput) => request<MovieSoundtrackCue>(`/api/movie-studio/projects/${projectId}/soundtrack/cues`, { method: "POST", body: JSON.stringify(input) }, true),
  getMovieSoundtrackCue: (cueId: string) => request<MovieSoundtrackCue>(`/api/movie-studio/soundtrack/cues/${cueId}`),
  updateMovieSoundtrackCue: (cueId: string, input: Partial<MovieSoundtrackCueInput>) => request<MovieSoundtrackCue>(`/api/movie-studio/soundtrack/cues/${cueId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  createMovieSoundtrackCueVersion: (cueId: string, input: { label: string; arrangementIntent?: string | null; mood?: string | null; intensity?: number | null; assetId?: string | null }) => request<MovieSoundtrackCue>(`/api/movie-studio/soundtrack/cues/${cueId}/versions`, { method: "POST", body: JSON.stringify(input) }, true),
  reviewMovieSoundtrackVersion: (versionId: string, input: { decision: string; comment?: string | null }) => request<MovieSoundtrackCue>(`/api/movie-studio/soundtrack/versions/${versionId}/review`, { method: "POST", body: JSON.stringify(input) }, true),
  // Captions.
  getMovieCaptionTracks: (projectId: string) => request<MovieCaptionTrack[]>(`/api/movie-studio/projects/${projectId}/caption-tracks`),
  getMovieCaptionTimeline: (projectId: string) => request<MovieCaptionTimeline>(`/api/movie-studio/projects/${projectId}/caption-timeline`),
  getMovieCaptionTrack: (trackId: string) => request<MovieCaptionTrack>(`/api/movie-studio/caption-tracks/${trackId}`),
  createMovieCaptionTrack: (projectId: string, input: { name: string; trackType?: string; language?: string; isRtl?: boolean; isDefault?: boolean }) => request<MovieCaptionTrack>(`/api/movie-studio/projects/${projectId}/caption-tracks`, { method: "POST", body: JSON.stringify(input) }, true),
  addMovieCaptionCue: (trackId: string, input: { startTimecode: string; endTimecode: string; text: string; speakerName?: string | null }) => request<MovieCaptionCue>(`/api/movie-studio/caption-tracks/${trackId}/cues`, { method: "POST", body: JSON.stringify(input) }, true),
  updateMovieCaptionCue: (cueId: string, input: { startTimecode: string; endTimecode: string; text: string; speakerName?: string | null }) => request<MovieCaptionCue>(`/api/movie-studio/caption-cues/${cueId}`, { method: "PATCH", body: JSON.stringify(input) }, true),
  deleteMovieCaptionCue: (cueId: string) => request<void>(`/api/movie-studio/caption-cues/${cueId}`, { method: "DELETE" }, true),
  movieCaptionExportUrl: (trackId: string, format = "srt") => `${API_URL}/api/movie-studio/caption-tracks/${trackId}/export?format=${encodeURIComponent(format)}`,
  // Dialogue lines and takes.
  getMovieClipDialogue: (clipId: string) => request<MovieDialogueClip>(`/api/movie-studio/clips/${clipId}/dialogue`),
  addMovieDialogueLine: (clipId: string, input: { movieCharacterId?: string | null; speakerName: string; language?: string; text: string; startMilliseconds: number; endMilliseconds: number; deliveryNotes?: string | null }) => request<MovieDialogueLine>(`/api/movie-studio/clips/${clipId}/dialogue`, { method: "POST", body: JSON.stringify(input) }, true),
  queueMovieDialogueTake: (lineId: string, input: { label?: string | null; voiceStyle?: string | null }, idempotencyKey = requestId()) => request<{ take: MovieDialogueTake; job: GenerationJob }>(`/api/movie-studio/dialogue/${lineId}/takes`, generationInit({ method: "POST", body: JSON.stringify(input) }, idempotencyKey), true),
  approveMovieDialogueTake: (takeId: string, input: { decision: "Approved" | "Rejected"; comment?: string | null }) => request<MovieDialogueLine>(`/api/movie-studio/dialogue/takes/${takeId}/approval`, { method: "POST", body: JSON.stringify(input) }, true),
  selectMovieDialogueTake: (takeId: string) => request<MovieDialogueLine>(`/api/movie-studio/dialogue/takes/${takeId}/select`, { method: "POST" }, true),
  // Bounded take selects.
  getMovieTakeSelects: (projectId: string, takeId: string) => request<MovieTakeSelectRecord[]>(`/api/movie-studio/projects/${projectId}/takes/${takeId}/selects`),
  createMovieTakeSelect: (projectId: string, takeId: string, input: { label: string; startMilliseconds: number; endMilliseconds: number; notes?: string | null }) => request<MovieTakeSelectRecord>(`/api/movie-studio/projects/${projectId}/takes/${takeId}/selects`, { method: "POST", body: JSON.stringify(input) }, true),
  reviewMovieTakeSelect: (projectId: string, takeId: string, selectId: string, input: { decision: "Approved" | "Rejected"; comment?: string | null }) => request<MovieTakeSelectRecord>(`/api/movie-studio/projects/${projectId}/takes/${takeId}/selects/${selectId}/review`, { method: "POST", body: JSON.stringify(input) }, true),
  // Collaboration, review and credits.
  getMovieCollaboration: (projectId: string) => request<MovieCollaboration>(`/api/movie-studio/projects/${projectId}/collaboration`),
  addMovieTeamMember: (projectId: string, input: { userId: string; role: string; permissions?: string[] | null }) => request<MovieCollaboration>(`/api/movie-studio/projects/${projectId}/collaboration/team`, { method: "POST", body: JSON.stringify(input) }, true),
  removeMovieTeamMember: (projectId: string, memberId: string) => request<MovieCollaboration>(`/api/movie-studio/projects/${projectId}/collaboration/team/${memberId}`, { method: "DELETE" }, true),
  addMovieComment: (projectId: string, input: { targetType: string; targetId: string; body: string; parentCommentId?: string | null; mentionedUserIds?: string[] | null }) => request<MovieCollaboration>(`/api/movie-studio/projects/${projectId}/collaboration/comments`, { method: "POST", body: JSON.stringify(input) }, true),
  resolveMovieComment: (projectId: string, commentId: string) => request<MovieCollaboration>(`/api/movie-studio/projects/${projectId}/collaboration/comments/${commentId}/resolve`, { method: "POST" }, true),
  requestMovieReview: (projectId: string, input: { targetType: string; targetId: string; reviewerUserId: string; isFinal: boolean; requestNote?: string | null }) => request<MovieCollaboration>(`/api/movie-studio/projects/${projectId}/collaboration/reviews`, { method: "POST", body: JSON.stringify(input) }, true),
  decideMovieReview: (projectId: string, reviewId: string, input: { status: string; decisionNote?: string | null }) => request<MovieCollaboration>(`/api/movie-studio/projects/${projectId}/collaboration/reviews/${reviewId}/decision`, { method: "POST", body: JSON.stringify(input) }, true),
};
