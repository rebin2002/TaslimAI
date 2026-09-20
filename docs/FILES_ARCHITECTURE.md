# Taslim file context architecture

Batch 3.5 adds a provider-neutral file foundation for project resources and chat attachments. Files are stored as metadata in PostgreSQL and bytes through an `IFileStorageService` boundary. Local development uses the filesystem under `Files:LocalRootPath`; Production selects the explicit `S3Compatible` boundary and requires a persistent object-storage adapter and credentials before uploads are enabled.

## Storage and ownership

`StoredFile` records workspace and uploader ownership, optional project or conversation scope, original and generated storage names, content type, extension, size, provider, storage key, lifecycle status, extraction status, bounded extracted text, and processing timestamps. File bytes are never stored in PostgreSQL. The generated storage name is a GUID-based key; the original filename is sanitized and retained only for display.

Project files are listed at `GET /api/workspaces/{workspaceId}/files?projectId={projectId}` and uploaded through the CSRF-protected multipart endpoint `POST /api/workspaces/{workspaceId}/files`. File deletion is a CSRF-protected soft-delete metadata transition plus provider-byte deletion. A normalized `ChatMessageAttachment` join table connects selected files to user messages without embedding binary data or storage keys in the chat payload.

## Validation and extraction

The API accepts PDF, DOCX, TXT, Markdown, CSV, XLSX, JPEG, PNG, and WebP. Extension, declared content type, size, and signatures are validated before storage. The default maximum file size is 25 MB and the default maximum attachment count is five per message; both are configuration-backed. PDF, DOCX, TXT, Markdown, CSV, and XLSX files are extracted server-side into bounded text. Images are retained as binary assets and are passed to a vision-capable provider only when the selected provider/model supports image input.

Extraction is best effort and safe: failed extraction changes the file status to `Failed`, does not expose parser exceptions to the browser, and does not inject untrusted parser diagnostics into model context. Text is normalized, bounded by `Files:MaxExtractedTextCharacters`, and then trimmed again by the AI context builder using `Ai:FileContextBudgetTokens`. Project files and personal workspace files are not automatically injected into every conversation; only files explicitly selected for the current message are attached.

## API and AI boundary

The frontend uploads a file and receives safe metadata only. It sends selected opaque file IDs with the chat request. The API verifies workspace, user, project, conversation, readiness, and attachment-count constraints, then resolves text or image bytes server-side. The AI Core receives provider-neutral `AiFileContext` values. The OpenAI adapter maps text files to bounded system context and images to multimodal input items; the browser never receives provider keys, storage credentials, storage keys, extracted text, or binary data from the upload flow.

## Production configuration

The repository includes a local storage implementation for development and tests and an explicit unavailable-provider implementation for the `S3Compatible` production boundary. This keeps the API deployable without silently writing user files to ephemeral Railway container storage. Connect a persistent S3-compatible provider by implementing `IFileStorageService` or wiring an approved adapter, then set the server-only variables below on Taslim API. They must never be placed on Taslim Web or passed as Docker build arguments.

```text
Files__StorageProvider=S3Compatible
Files__S3Endpoint=https://object-storage.example
Files__S3Bucket=taslim-files
Files__S3AccessKey=<server-only>
Files__S3SecretKey=<server-only>
Files__MaxFileSizeBytes=26214400
Files__MaxAttachmentsPerMessage=5
Files__MaxExtractedTextCharacters=80000
Ai__FileContextBudgetTokens=4000
```

The additive migration is `AddStoredFilesAndChatAttachments`. It creates `StoredFiles` and `ChatMessageAttachments` with restricted ownership relationships, deterministic indexes, and a unique storage-key constraint. Existing authentication, CSRF, workspace authorization, AI routing, usage accounting, and conversation persistence remain the source of truth for their respective concerns.
