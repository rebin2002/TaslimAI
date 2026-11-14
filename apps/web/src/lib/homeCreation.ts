export const attachmentOnlyCreationPrompt = "Please review the attached files.";

export function resolveHomeCreationPrompt(value: string, attachmentCount: number): string | null {
  const prompt = value.trim();
  if (prompt) return prompt;
  return attachmentCount > 0 ? attachmentOnlyCreationPrompt : null;
}

export function canSubmitHomeCreation(value: string, attachmentCount: number): boolean {
  return resolveHomeCreationPrompt(value, attachmentCount) !== null;
}

/** Synchronous single-flight admission for Home create, independent of React renders. */
export function createHomeCreationSubmissionGate() {
  let busy = false;
  return {
    tryStart(): boolean {
      if (busy) return false;
      busy = true;
      return true;
    },
    finish(): void { busy = false; },
  };
}

/**
 * IME-aware Enter admission for the Home creation composer. Enter is used to
 * confirm candidate characters in Chinese/Japanese/Korean input; only a
 * completed plain Enter may submit. The caller still enforces the same
 * busy/content guard as the Create button.
 */
export function isHomeCreationSubmitKey(event: Readonly<{
  key: string;
  shiftKey: boolean;
  isComposing: boolean;
  keyCode: number;
}>): boolean {
  return event.key === "Enter" && !event.shiftKey
    && !event.isComposing && event.keyCode !== 229;
}
