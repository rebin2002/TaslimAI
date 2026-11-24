"use client";

import { useEffect, useLayoutEffect, useRef, type RefObject } from "react";

const FOCUSABLE_SELECTOR = [
  'a[href]:not([tabindex="-1"])',
  'button:not([disabled]):not([tabindex="-1"])',
  'input:not([disabled]):not([tabindex="-1"])',
  'select:not([disabled]):not([tabindex="-1"])',
  'textarea:not([disabled]):not([tabindex="-1"])',
  '[tabindex]:not([tabindex="-1"])',
].join(",");

const FORM_CONTROL_TAGS = new Set(["BUTTON", "INPUT", "SELECT", "TEXTAREA"]);

type VisibilityStyleReader = (element: Element) => { display: string; visibility: string };

/**
 * True when the browser itself can move sequential focus navigation into this
 * element: not hidden (attribute or ancestor attribute), not inside an
 * `aria-hidden` or `inert` subtree, not disabled (directly or through a
 * disabled `fieldset`), not a hidden input, not negatively tabbable, and
 * actually rendered (CSS `display`/`visibility` respected through ancestors).
 * A DOM surface only needs to expose `getComputedStyle` when the caller wants
 * the non-`checkVisibility` fallback exercised; browsers take the fast path.
 */
export function isTabbableElement(element: HTMLElement, readStyle?: VisibilityStyleReader): boolean {
  if (element.hidden || element.closest("[hidden]")) return false;
  if (element.getAttribute("aria-hidden") === "true" || element.closest('[aria-hidden="true"]')) return false;
  if (element.closest("[inert]")) return false;
  if (FORM_CONTROL_TAGS.has(element.tagName)) {
    const control = element as HTMLButtonElement | HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement;
    if (control.disabled) return false;
    if (element.getAttribute("disabled") !== null) return false;
    // A disabled fieldset un-focuses every contained form control except the
    // ones inside its legend (HTML living-standard sequential navigation).
    const fieldset = element.closest("fieldset[disabled]");
    if (fieldset && !element.closest("legend")) return false;
    if (element.tagName === "INPUT" && (element as HTMLInputElement).type === "hidden") return false;
  }
  const tabindex = element.getAttribute("tabindex");
  if (tabindex !== null) {
    const parsed = Number.parseInt(tabindex, 10);
    if (Number.isFinite(parsed) && parsed < 0) return false;
  }
  const check = element.checkVisibility;
  if (typeof check === "function") {
    try {
      // Engines with the option answer display, visibility and hidden-subtree
      // state in one call; a false result means the browser itself refuses
      // sequential focus into this element.
      return check.call(element, { visibilityProperty: true });
    } catch {
      // Older engine without option support: fall through to the walk below.
    }
  }
  const styleReader = readStyle ?? ((node: Element) => {
    const style = window.getComputedStyle(node);
    return { display: style.display, visibility: style.visibility };
  });
  for (let node: HTMLElement | null = element; node !== null; node = node.parentElement) {
    const computed = styleReader(node);
    if (computed.display === "none") return false;
    if (computed.visibility === "hidden" || computed.visibility === "collapse") return false;
  }
  return true;
}

export type DialogHandle = { readonly token: symbol };

// Single stack of mounted, open dialog-hook instances. Exactly one instance —
// the top of the stack — answers document-level focusin/keydown traffic at a
// time, which makes Escape double-close and inter-dialog focusin redirection
// (each instance pulling focus back and triggering the other again) impossible
// when several dialog hooks are mounted at once, as AssetsView can do with its
// filter sheet and asset edit modal.
const dialogStack: DialogHandle[] = [];

export function openDialog(handle: DialogHandle): void {
  if (!dialogStack.includes(handle)) dialogStack.push(handle);
}

export function closeDialog(handle: DialogHandle): void {
  const index = dialogStack.indexOf(handle);
  if (index >= 0) dialogStack.splice(index, 1);
}

export function raiseDialog(handle: DialogHandle): void {
  if (!dialogStack.includes(handle)) return;
  closeDialog(handle);
  dialogStack.push(handle);
}

export function isTopDialog(handle: DialogHandle): boolean {
  return dialogStack[dialogStack.length - 1] === handle;
}

export function dialogStackSize(): number {
  return dialogStack.length;
}

/**
 * The document-level keydown decision for one dialog instance: Tab/Escape are
 * only processed by the topmost open dialog, so a stale lower instance can
 * never steal the cycle or close in addition to the visible front dialog.
 */
export function shouldHandleDialogKey(handle: DialogHandle, key: string): boolean {
  if (key !== "Escape" && key !== "Tab") return false;
  return isTopDialog(handle);
}

function focusableElements(container: HTMLElement) {
  return Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter(
    (element) => isTabbableElement(element),
  );
}

type FocusTargetDescriptor = {
  tagName: string;
  id: string;
  href: string | null;
  label: string;
};

function describeFocusTarget(element: HTMLElement): FocusTargetDescriptor {
  return {
    tagName: element.tagName.toLowerCase(),
    id: element.id,
    href: element.tagName === "A" ? element.getAttribute("href") : null,
    label: (element.getAttribute("aria-label") ?? element.textContent ?? "").replace(/\s+/g, " ").trim(),
  };
}

function resolveFocusTarget(element: HTMLElement | null, descriptor: FocusTargetDescriptor | null): HTMLElement | null {
  if (element?.isConnected) return element;
  if (!descriptor) return null;
  if (descriptor.id) {
    const elementById = document.getElementById(descriptor.id);
    if (elementById instanceof HTMLElement) return elementById;
  }

  const candidates = Array.from(document.querySelectorAll<HTMLElement>(descriptor.tagName));
  return candidates.find((candidate) => {
    if (descriptor.href !== null && candidate.getAttribute("href") !== descriptor.href) return false;
    const label = (candidate.getAttribute("aria-label") ?? candidate.textContent ?? "").replace(/\s+/g, " ").trim();
    return label === descriptor.label;
  }) ?? null;
}

/**
 * Keeps keyboard/programmatic focus inside an open modal and returns it to the
 * opening control after dismissal. Callers provide dialog semantics and labels.
 */
export function useDialogAccessibility<T extends HTMLElement>(open: boolean, onClose: () => void, restoreFocusRef?: RefObject<HTMLElement | null>): RefObject<T | null> {
  const dialogRef = useRef<T | null>(null);
  const openerRef = useRef<HTMLElement | null>(null);
  const openerDescriptorRef = useRef<FocusTargetDescriptor | null>(null);
  const onCloseRef = useRef(onClose);
  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useLayoutEffect(() => {
    if (!open || !dialogRef.current) return;

    const dialog = dialogRef.current;
    const handle: DialogHandle = { token: Symbol("taslim-dialog") };
    openDialog(handle);
    const activeElement = document.activeElement;
    const candidateOpener = restoreFocusRef?.current
      ?? (activeElement instanceof HTMLElement && activeElement !== document.body ? activeElement : null);
    const opener = candidateOpener?.isConnected && !dialog.contains(candidateOpener) ? candidateOpener : null;
    openerRef.current = opener;
    openerDescriptorRef.current = opener ? describeFocusTarget(opener) : null;
    const focusInitial = window.setTimeout(() => {
      if (!dialog.isConnected) return;
      const marked = dialog.querySelector<HTMLElement>("[data-dialog-autofocus]");
      const initial = marked && isTabbableElement(marked)
        ? marked
        : focusableElements(dialog)[0] ?? dialog;
      initial.focus();
    }, 0);

    function focusBoundary(backwards: boolean) {
      const focusable = focusableElements(dialog);
      const boundary = backwards ? focusable[focusable.length - 1] : focusable[0];
      (boundary ?? dialog).focus();
    }

    function handleFocusIn(event: FocusEvent) {
      const target = event.target;
      if (!(target instanceof Node)) return;
      if (dialog.contains(target)) {
        // Whichever dialog actually receives focus is the one the user works
        // in; it becomes the sole handler until focus moves again.
        raiseDialog(handle);
        return;
      }
      if (!isTopDialog(handle)) return;
      focusBoundary(false);
    }

    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        if (!shouldHandleDialogKey(handle, "Escape")) return;
        event.preventDefault();
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      if (!shouldHandleDialogKey(handle, "Tab")) return;

      const focusable = focusableElements(dialog);
      if (!focusable.length) {
        event.preventDefault();
        dialog.focus();
        return;
      }

      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const active = document.activeElement;
      const activeIndex = focusable.indexOf(active instanceof HTMLElement ? active : dialog);
      if (!dialog.contains(active)) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus();
      } else if (event.shiftKey && activeIndex <= 0) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (activeIndex === -1 || activeIndex === focusable.length - 1)) {
        event.preventDefault();
        first.focus();
      }
    }

    document.addEventListener("focusin", handleFocusIn, true);
    document.addEventListener("keydown", handleKeyDown, true);
    return () => {
      window.clearTimeout(focusInitial);
      document.removeEventListener("focusin", handleFocusIn, true);
      document.removeEventListener("keydown", handleKeyDown, true);
      closeDialog(handle);
      const opener = openerRef.current;
      const openerDescriptor = openerDescriptorRef.current;
      if (opener || openerDescriptor) {
        window.setTimeout(() => resolveFocusTarget(opener, openerDescriptor)?.focus(), 0);
      }
    };
  }, [open, restoreFocusRef]);

  return dialogRef;
}
