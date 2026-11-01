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

function focusableElements(container: HTMLElement) {
  return Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)).filter(
    (element) => !element.hidden && element.getAttribute("aria-hidden") !== "true",
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
    const activeElement = document.activeElement;
    const candidateOpener = restoreFocusRef?.current
      ?? (activeElement instanceof HTMLElement && activeElement !== document.body ? activeElement : null);
    const opener = candidateOpener?.isConnected && !dialog.contains(candidateOpener) ? candidateOpener : null;
    openerRef.current = opener;
    openerDescriptorRef.current = opener ? describeFocusTarget(opener) : null;
    const focusInitial = window.setTimeout(() => {
      if (!dialog.isConnected) return;
      const initial = dialog.querySelector<HTMLElement>("[data-dialog-autofocus]") ?? focusableElements(dialog)[0] ?? dialog;
      initial.focus();
    }, 0);

    function focusBoundary(backwards: boolean) {
      const focusable = focusableElements(dialog);
      const boundary = backwards ? focusable[focusable.length - 1] : focusable[0];
      (boundary ?? dialog).focus();
    }

    function handleFocusIn(event: FocusEvent) {
      const target = event.target;
      if (!(target instanceof Node) || dialog.contains(target)) return;
      focusBoundary(false);
    }

    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      if (event.key !== "Tab") return;

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
      const opener = openerRef.current;
      const openerDescriptor = openerDescriptorRef.current;
      if (opener || openerDescriptor) {
        window.setTimeout(() => resolveFocusTarget(opener, openerDescriptor)?.focus(), 0);
      }
    };
  }, [open, restoreFocusRef]);

  return dialogRef;
}
