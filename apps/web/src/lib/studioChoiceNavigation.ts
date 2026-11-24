/**
 * Roving-focus index model for a single-choice studio option group
 * (`role="radiogroup"` with one tabbable radio).
 *
 * A preset group selects exactly one option, so arrow keys move the selection itself, not focus
 * past the group: the index returned here drives both the state change and the focus of the
 * matching radio. The navigation wraps around the ends and jumps to the ends with Home and End,
 * matching the pattern already accepted for the Voice Studio choice grids
 * (`nextVoiceChoiceIndex`). Direction keys are kept in logical order across locales: Arabic and
 * Kurdish Sorani render the options right-to-left, and the APG expects Up/Down (and the axis
 * keys) to step the option list, not the visual position, so a keyboard model stays identical
 * for EN, AR, and Sorani users.
 *
 * Returns null for any key that is not a choice-navigation key, or for a group the component
 * cannot locate the current value in, so the caller can leave the event untouched.
 */
export function nextStudioChoiceIndex(currentIndex: number, key: string, count: number): number | null {
  if (count < 1 || currentIndex < 0 || currentIndex >= count) return null;
  if (key === "Home") return 0;
  if (key === "End") return count - 1;
  if (key === "ArrowRight" || key === "ArrowDown") return (currentIndex + 1) % count;
  if (key === "ArrowLeft" || key === "ArrowUp") return (currentIndex - 1 + count) % count;
  return null;
}
