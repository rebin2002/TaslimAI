/**
 * Document Studio unsaved-draft persistence.
 *
 * Gap this module closes: the compose brief - description, title, audience,
 * advanced instructions and every guided choice - lived only in
 * `DocumentStudioView` state, and nothing exists on the server until a
 * generation job is submitted. Switching tabs away from the document editor and
 * back unmounts and remounts the view, so work the user had never sent was
 * discarded and the editor returned empty. Sibling studios already keep their
 * unsent brief browser-local (`presentationStudioState.ts`,
 * `socialStudioState.ts`, `productAdDraftState.ts`); this is the same contract
 * for the document editor, split into its own module so the draft lifecycle can
 * be read and tested apart from the job-lifecycle helpers.
 *
 * The draft is browser-local and keyed by the workspace that owns it - the same
 * boundary as the active generation job in `documentStudioState.ts` - so one
 * workspace's private brief is never replayed into another. Reading is
 * fail-closed: a stored value that is not a readable draft restores defaults,
 * text and selections are clamped to what the editor can hold, and a brief that
 * still holds no user work is treated as no draft at all.
 */

const draftStoragePrefix = "taslim:document-draft:";
const documentDraftTypes = new Set(["auto", "report", "proposal", "business_letter", "company_profile", "meeting_minutes", "article", "general"]);
const documentDraftLengths = new Set(["short", "standard", "detailed"]);
const documentDraftLanguages = new Set(["auto", "en", "ar", "ku"]);
const documentDraftOutputFormats = new Set(["docx", "pdf", "both"]);
const documentDraftTones = new Set(["professional", "formal", "friendly", "persuasive", "neutral"]);

type StorageLike = Pick<Storage, "getItem" | "setItem" | "removeItem">;

export type DocumentDraftState = {
  projectId: string;
  selected: string[];
  title: string;
  prompt: string;
  documentType: string;
  length: string;
  audience: string;
  additionalInstructions: string;
  language: string;
  outputFormat: string;
  tone: string;
  includeTableOfContents: boolean;
};

export function emptyDocumentDraftState(): DocumentDraftState {
  return {
    projectId: "",
    selected: [],
    title: "",
    prompt: "",
    documentType: "auto",
    length: "standard",
    audience: "",
    additionalInstructions: "",
    language: "auto",
    outputFormat: "both",
    tone: "professional",
    includeTableOfContents: true,
  };
}

export function documentDraftStorageKey(workspaceId: string) {
  return `${draftStoragePrefix}${encodeURIComponent(workspaceId)}`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function draftIdentifier(value: unknown) {
  return typeof value === "string" && value.trim().length > 0 && value.length <= 100 ? value : "";
}

function boundedDraftText(value: unknown, maxLength: number) {
  return typeof value === "string" ? value.slice(0, maxLength) : "";
}

export function parseDocumentDraftState(raw: string | null): DocumentDraftState {
  const fallback = emptyDocumentDraftState();
  if (!raw) return fallback;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!isRecord(parsed)) return fallback;
    const selected = Array.isArray(parsed.selected) ? [...new Set(parsed.selected.map(draftIdentifier).filter(Boolean))].slice(0, 5) : [];
    return {
      projectId: draftIdentifier(parsed.projectId),
      selected,
      title: boundedDraftText(parsed.title, 160),
      prompt: boundedDraftText(parsed.prompt, 8_000),
      documentType: typeof parsed.documentType === "string" && documentDraftTypes.has(parsed.documentType) ? parsed.documentType : fallback.documentType,
      length: typeof parsed.length === "string" && documentDraftLengths.has(parsed.length) ? parsed.length : fallback.length,
      audience: boundedDraftText(parsed.audience, 400),
      additionalInstructions: boundedDraftText(parsed.additionalInstructions, 3_000),
      language: typeof parsed.language === "string" && documentDraftLanguages.has(parsed.language) ? parsed.language : fallback.language,
      outputFormat: typeof parsed.outputFormat === "string" && documentDraftOutputFormats.has(parsed.outputFormat) ? parsed.outputFormat : fallback.outputFormat,
      tone: typeof parsed.tone === "string" && documentDraftTones.has(parsed.tone) ? parsed.tone : fallback.tone,
      includeTableOfContents: parsed.includeTableOfContents === false ? false : fallback.includeTableOfContents,
    };
  } catch {
    return fallback;
  }
}

export function serializeDocumentDraftState(state: DocumentDraftState) {
  return JSON.stringify({
    projectId: draftIdentifier(state.projectId),
    selected: [...new Set(state.selected.map(draftIdentifier).filter(Boolean))].slice(0, 5),
    title: boundedDraftText(state.title, 160),
    prompt: boundedDraftText(state.prompt, 8_000),
    documentType: documentDraftTypes.has(state.documentType) ? state.documentType : "auto",
    length: documentDraftLengths.has(state.length) ? state.length : "standard",
    audience: boundedDraftText(state.audience, 400),
    additionalInstructions: boundedDraftText(state.additionalInstructions, 3_000),
    language: documentDraftLanguages.has(state.language) ? state.language : "auto",
    outputFormat: documentDraftOutputFormats.has(state.outputFormat) ? state.outputFormat : "both",
    tone: documentDraftTones.has(state.tone) ? state.tone : "professional",
    includeTableOfContents: state.includeTableOfContents === true,
  });
}

/** A brief that still matches every default holds nothing the user wrote, so it
 * is not a draft: nothing is kept for it. */
export function isDocumentDraftEmpty(state: DocumentDraftState) {
  return serializeDocumentDraftState(state) === serializeDocumentDraftState(emptyDocumentDraftState());
}

function browserSessionStorage(): StorageLike | undefined {
  if (typeof window === "undefined") return undefined;
  try {
    return window.sessionStorage;
  } catch {
    return undefined;
  }
}

export function readDocumentDraftState(workspaceId: string, storage: StorageLike | undefined = browserSessionStorage()): DocumentDraftState {
  if (!storage || !workspaceId.trim()) return emptyDocumentDraftState();
  try {
    const key = documentDraftStorageKey(workspaceId);
    const raw = storage.getItem(key);
    if (raw === null) return emptyDocumentDraftState();
    const draft = parseDocumentDraftState(raw);
    // A stored value that yields nothing restorable - unreadable, or a brief
    // that has since been emptied - is dropped instead of being kept as trash
    // another mount might read back.
    if (isDocumentDraftEmpty(draft)) storage.removeItem(key);
    return draft;
  } catch {
    return emptyDocumentDraftState();
  }
}

export function persistDocumentDraftState(workspaceId: string, state: DocumentDraftState, storage: StorageLike | undefined = browserSessionStorage()): void {
  if (!storage || !workspaceId.trim()) return;
  try {
    if (isDocumentDraftEmpty(state)) {
      storage.removeItem(documentDraftStorageKey(workspaceId));
      return;
    }
    storage.setItem(documentDraftStorageKey(workspaceId), serializeDocumentDraftState(state));
  } catch {
    // Storage may be unavailable or full; the in-memory brief stays usable.
  }
}

export function clearDocumentDraftState(workspaceId: string, storage: StorageLike | undefined = browserSessionStorage()): void {
  if (!storage || !workspaceId.trim()) return;
  try {
    storage.removeItem(documentDraftStorageKey(workspaceId));
  } catch {
    // Clearing is best effort.
  }
}
