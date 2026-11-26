import type { Locale } from "@/lib/i18n";

export const attachmentOnlyCreationPrompt = "Please review the attached files.";

/** Keep attachment-only creation understandable in the active interface language. */
export const attachmentOnlyCreationPrompts: Readonly<Record<Locale, string>> = {
  en: attachmentOnlyCreationPrompt,
  ar: "يرجى مراجعة الملفات المرفقة.",
  ku: "تکایە پێداچوونەوە بە فایلە هاوپێچکراوەکاندا بکە.",
};

export function resolveHomeCreationPrompt(value: string, attachmentCount: number, locale: Locale = "en"): string | null {
  const prompt = value.trim();
  if (prompt) return prompt;
  return attachmentCount > 0 ? attachmentOnlyCreationPrompts[locale] : null;
}

export function canSubmitHomeCreation(value: string, attachmentCount: number): boolean {
  return resolveHomeCreationPrompt(value, attachmentCount) !== null;
}

/** Synchronous single-flight admission for Home create, independent of React renders. */
export function createHomeCreationSubmissionGate() {
  let busy = false;
  let cancelled = false;
  return {
    tryStart(): boolean {
      if (busy || cancelled) return false;
      busy = true;
      return true;
    },
    isActive(): boolean { return busy && !cancelled; },
    finish(): void { busy = false; },
    cancel(): void {
      cancelled = true;
      busy = false;
    },
  };
}

/** Do not submit on Shift+Enter or while an IME is confirming a candidate. */
export function isHomeCreationSubmitKey(event: Readonly<{
  key: string;
  shiftKey: boolean;
  isComposing: boolean;
  keyCode: number;
}>): boolean {
  return event.key === "Enter" && !event.shiftKey
    && !event.isComposing && event.keyCode !== 229;
}
