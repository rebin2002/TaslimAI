/**
 * Focus decisions shared by route recovery boundaries.
 *
 * When a Next.js error boundary renders, React unmounts the crashed subtree
 * together with the element that held focus - on a phone that is usually the
 * control the user had just tapped. Focus then falls back to `document.body`,
 * so the retry action is no longer next in tab order and the assertive
 * recovery message is easy to miss. The boundary moves focus onto its own
 * panel instead; this module owns the decision so it can be verified without a
 * browser or a real navigation.
 */

export type RecoveryFocusPanel = {
  contains: (node: Node | null) => boolean;
  focus: () => void;
};

/**
 * True when focus currently sits outside the panel - including the `body`
 * fallback left behind by the unmounted subtree - and therefore has to be
 * moved into it. A panel that already holds focus is left alone so a
 * re-render cannot steal focus from a control the user is on.
 */
export function shouldRestoreRecoveryFocus(
  panel: RecoveryFocusPanel | null | undefined,
  activeElement: Node | null | undefined,
): boolean {
  if (!panel) return false;
  if (activeElement && panel.contains(activeElement)) return false;
  return true;
}

/**
 * Move focus into a freshly mounted recovery panel and report whether focus
 * was moved, so the skipped case stays observable to callers and tests.
 */
export function restoreRecoveryFocus(
  panel: RecoveryFocusPanel | null | undefined,
  activeElement: Node | null | undefined,
): boolean {
  if (!panel) return false;
  if (!shouldRestoreRecoveryFocus(panel, activeElement)) return false;
  panel.focus();
  return true;
}
