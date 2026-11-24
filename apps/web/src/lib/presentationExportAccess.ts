/**
 * Naming and focus decisions for the Presentation result's download/export menu.
 *
 * The `presentation-output-actions` toolbar renders one button per downloadable
 * rendition of the finished deck. Until now every one of them was
 *
 *   <button disabled={working} aria-busy={working}><Download /> PPTX</button>
 *
 * which costs a keyboard or screen-reader user two concrete things:
 *
 * 1. The only accessible name the control had was the format token `PPTX`. It
 *    never said what activating the button does (WCAG 2.4.6), and it is the same
 *    string for every rendition, so a result that carries more than one
 *    PowerPoint file - the shape `parsePresentationJobResult` deliberately
 *    accepts, see `presentationStudioState.test.ts` - presented two buttons no
 *    screen reader can tell apart. The file name was already on the representation
 *    and simply never reached the name.
 * 2. `disabled` applies to the button the user has just activated, and browsers
 *    both take a disabled element out of the accessibility tree and blur it. Focus
 *    therefore fell back to `document.body` for the whole blob download and never
 *    came back: reaching the deck navigator or the "Open assets" link afterwards
 *    meant re-traversing the app shell (WCAG 2.4.3 / 2.4.7). PR #975 recorded
 *    exactly this harm for the Image Studio project selector and its retry and
 *    cancel controls, and fixed it by keeping the control mounted and focusable
 *    while reporting the transient state through `aria-busy` / `aria-disabled`
 *    instead of the attribute. This module owns the same decision for the export
 *    menu so it can be verified without a browser or a provider, the way
 *    `chatThreadFocus.ts` owns the Chat thread switcher's.
 *
 * Nothing here touches the DOM, React, a translation catalog or a network client:
 * the component passes in already-resolved strings and the in-flight flags.
 */

/** Rendition types the export menu offers for download. */
export const presentationExportTypes = ["pptx"] as const;

/** One rendition as the export menu needs it. */
export type PresentationExportItem = { id: string; type: string };

/** How an export control has to report the download that is in flight. */
export type PresentationExportControlState = {
  /** True on the control whose own download is running. */
  busy: boolean;
  /** True on a control that has to wait for that download to settle. */
  blocked: boolean;
};

/**
 * The renditions the export menu renders, in result order.
 *
 * Kept as the single definition of "downloadable" so the toolbar's buttons and any
 * statement about them cannot drift into disagreeing with each other the way the
 * inline `type === "pptx"` filter did.
 */
export function selectPresentationExports<T extends PresentationExportItem>(
  representations: readonly T[] | undefined | null,
): T[] {
  const types: readonly string[] = presentationExportTypes;
  return (representations ?? []).filter((representation) => types.includes(representation.type));
}

/**
 * The caption drawn on an export button.
 *
 * The accessible name is built around this exact string, so deriving both from
 * the rendition keeps the visible label and the name from drifting apart.
 */
export function presentationExportCaption(type: string): string {
  return type.toUpperCase();
}

/**
 * The accessible name of one export button.
 *
 * It keeps the visible caption inside the name (WCAG 2.5.3 - a label-in-name
 * mismatch makes voice control users say a word the control will not answer to),
 * states the action, and ends with the specific file so two renditions of the same
 * format stay distinguishable.
 *
 * The name deliberately cannot see the in-flight state. Re-naming a control that
 * already holds focus is announced as though a different control had appeared, so
 * progress has to be carried by `aria-busy` alone.
 */
export function presentationExportAccessibleName(args: {
  action: string;
  caption: string;
  fileName?: string | null;
}): string {
  const name = `${args.action} ${args.caption}`;
  const fileName = args.fileName?.trim();
  return fileName ? `${name}: ${fileName}` : name;
}

/**
 * How one export control reports the download currently in flight.
 *
 * Only the activated control is busy; its siblings stay focusable and say they are
 * unavailable. Neither flag is the `disabled` attribute, which is the thing that
 * used to steal focus - both are states an assistive technology can read while the
 * element keeps its place in the tab order.
 */
export function resolvePresentationExportControl(args: {
  working: boolean;
  pendingExportId: string | null;
  exportId: string;
}): PresentationExportControlState {
  const busy = args.working && args.pendingExportId === args.exportId;
  return { busy, blocked: args.working && !busy };
}

/**
 * Whether activating this control may start a download.
 *
 * Refusing the activation in the handler is what preserves the guarantee the
 * `disabled` attribute used to provide - one export in flight per result view -
 * without taking the focused element out of the accessibility tree.
 */
export function canActivatePresentationExport(state: PresentationExportControlState): boolean {
  return !state.busy && !state.blocked;
}
