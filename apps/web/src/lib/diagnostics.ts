export function logChatDiagnostic(message: string) {
  if (typeof window !== "undefined") console.info(message);
}

export function safeStreamError(error: unknown): string {
  if (error instanceof Error && error.name === "SseProtocolError") return "SSE_PROTOCOL_ERROR";
  if (error && typeof error === "object" && "code" in error && typeof error.code === "string") return error.code;
  if (error && typeof error === "object" && "status" in error && typeof error.status === "number") return `HTTP_${error.status}`;
  return "STREAM_REQUEST_FAILED";
}
