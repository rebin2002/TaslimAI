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
    // ones inside *that fieldset's own* legend (HTML living-standard sequential
    // navigation). Two subtleties decide the answer. "Its own" is load-bearing:
    // a legend belonging to a nested fieldset, or a stray second legend child,
    // is not the element the exemption is written about. And the constraint is
    // applied per disabled fieldset ancestor, not once for the nearest one: a
    // control that escapes an inner disabled fieldset by sitting in that
    // fieldset's legend is still inside an outer disabled fieldset, outside the
    // outer legend, and Chromium refuses to focus it. Admitting such a control
    // would let the focus trap enumerate something the browser itself will not
    // tab into.
    for (let ancestor = element.parentElement; ancestor !== null; ancestor = ancestor.parentElement) {
      if (ancestor.tagName !== "FIELDSET" || ancestor.getAttribute("disabled") === null) continue;
      if (!escapesDisabledFieldset(ancestor, element)) return false;
    }
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

/**
 * True when the control escapes `fieldset` because it sits inside that
 * fieldset's own legend - its first `legend` child. Measured in headless
 * Chromium by the #1226 lane: a control nested any deeper (a second legend
 * child, or a legend belonging to a nested fieldset) is skipped by the browser's
 * sequential-focus walk, and the bar applies once per disabled ancestor, which is
 * why the caller loops the ancestors instead of consulting `closest` once.
 *
 * The walk-up form is deliberate: it is exactly
 * `fieldset.querySelector(":scope > legend")?.contains(control)` - the shape the
 * #1226 lane measured - expressed with `parentElement` and `children` only, so it
 * also runs on the dependency-free DOM stubs this repo's unit tests use (which
 * implement neither `querySelector` nor `contains`, and whose frozen contract file
 * must keep passing unchanged).
 */
function escapesDisabledFieldset(fieldset: Element, control: Element): boolean {
  let legend: Element | null = null;
  for (const child of Array.from(fieldset.children)) {
    if (child.tagName === "LEGEND") {
      legend = child;
      break;
    }
  }
  if (legend === null) return false;
  for (let node = control.parentElement; node !== null && node !== fieldset; node = node.parentElement) {
    if (node === legend) return true;
  }
  return false;
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
    // True between a keydown the trap consumed (boundary wrap/rescue) and the
    // matching Tab keyup, whose browser default must be suppressed too.
    let trapConsumedTab = false;
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
      // A fresh Tab keydown begins a new press; any pending consumed-press
      // guard from keyup repeats must not leak into it.
      if (event.key === "Tab") trapConsumedTab = false;
      if (event.key === "Escape") {
        if (!shouldHandleDialogKey(handle, "Escape")) return;
        event.preventDefault();
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      // Gate on the ACTUAL key: a Shift+Tab chord first delivers a bare
      // keydown(Shift), and hardcoding "Tab" here let that modifier press run
      // the boundary logic, wrapping focus on its own so the later Tab keydown
      // walked backward from the wrap anchor one step short (hosted Browser
      // E2E: Cancel focused instead of Create at the backward wrap).
      if (!shouldHandleDialogKey(handle, event.key)) return;

      const focusable = focusableElements(dialog);
      if (!focusable.length) {
        event.preventDefault();
        trapConsumedTab = true;
        dialog.focus();
        return;
      }

      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const active = document.activeElement;
      const activeIndex = focusable.indexOf(active instanceof HTMLElement ? active : dialog);
      if (!dialog.contains(active)) {
        event.preventDefault();
        trapConsumedTab = true;
        (event.shiftKey ? last : first).focus();
      } else if (event.shiftKey && activeIndex <= 0) {
        event.preventDefault();
        trapConsumedTab = true;
        last.focus();
      } else if (!event.shiftKey && (activeIndex === -1 || activeIndex === focusable.length - 1)) {
        event.preventDefault();
        trapConsumedTab = true;
        first.focus();
      }
    }

    // Belt-and-braces containment of a press whose keydown the trap consumed
    // (boundary wrap, stray-focus rescue, empty-dialog guard). Chromium
    // resolves sequential focus navigation in the keydown default action, so
    // the preventDefault calls above already contain every observed walk;
    // this guard suppresses a second default movement in engines that act on
    // keyup instead. It is armed ONLY by consumed keydowns: a normal
    // mid-cycle backward step is not consumed and its native movement must
    // stay intact.
    function handleKeyUp(event: KeyboardEvent) {
      if (event.key !== "Tab" || !trapConsumedTab) return;
      trapConsumedTab = false;
      event.preventDefault();
    }

    document.addEventListener("focusin", handleFocusIn, true);
    document.addEventListener("keydown", handleKeyDown, true);
    document.addEventListener("keyup", handleKeyUp, true);
    return () => {
      window.clearTimeout(focusInitial);
      document.removeEventListener("focusin", handleFocusIn, true);
      document.removeEventListener("keydown", handleKeyDown, true);
      document.removeEventListener("keyup", handleKeyUp, true);
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
