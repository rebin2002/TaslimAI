export type ChatAttachmentUploadGenerationRef = { current: number };

export function beginChatAttachmentUpload(ref: ChatAttachmentUploadGenerationRef) {
  ref.current += 1;
  return ref.current;
}

export function invalidateChatAttachmentUploads(ref: ChatAttachmentUploadGenerationRef) {
  ref.current += 1;
}

export function isCurrentChatAttachmentUpload(ref: ChatAttachmentUploadGenerationRef, generation: number) {
  return ref.current === generation;
}
