import type { User } from "./api";
import { localeTag, type Locale } from "./i18n";

/**
 * The mark shown when an account has no renderable first character: the brand
 * initial already used by the chat and preview identities.
 */
export const profileAvatarFallbackInitial = "T";

/** Only the display name is needed to draw the thumbnail; no other field is read. */
type ProfileAvatarSource = Pick<User, "displayName"> | null | undefined;

/**
 * A surrogate half on its own is not a character: it cannot be encoded as
 * UTF-8 and the browser replaces it with U+FFFD, so the thumbnail shows a
 * broken-glyph box instead of an initial.
 */
const unpairedSurrogate = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/g;

function isLoneSurrogateUnit(mark: string): boolean {
  const unit = mark.charCodeAt(0);
  return mark.length === 1 && unit >= 0xd800 && unit <= 0xdfff;
}

/**
 * The first character the reader actually perceives. Slicing a single UTF-16
 * code unit cuts every character above the Basic Multilingual Plane in half
 * and splits a joined emoji sequence at its zero-width joiners, so the mark is
 * taken as a whole cluster instead; a runtime without `Intl.Segmenter` still
 * gets whole code points from `Array.from`.
 */
function firstGrapheme(text: string): string {
  const Segmenter = (Intl as unknown as { Segmenter?: typeof Intl.Segmenter }).Segmenter;
  if (typeof Segmenter === "function") {
    for (const part of new Segmenter(undefined, { granularity: "grapheme" }).segment(text)) {
      return isLoneSurrogateUnit(part.segment) ? "" : part.segment;
    }
    return "";
  }
  const mark = Array.from(text)[0] ?? "";
  return isLoneSurrogateUnit(mark) ? "" : mark;
}

/**
 * Casing follows the interface language, because a locale-blind `toUpperCase`
 * renames letters that a reader of that language would not accept — a Kurdish
 * or Turkish account should not be initials'd with the wrong dotted or dotless
 * form. An unsupported tag degrades to the plain mapping rather than throwing
 * during render, which would blank the whole header.
 */
function upperCaseMark(mark: string, locale: Locale | string | undefined): string {
  const tag = locale ? localeTag(locale) : undefined;
  if (!tag) return mark.toUpperCase();
  try {
    return mark.toLocaleUpperCase(tag);
  } catch {
    return mark.toUpperCase();
  }
}

/**
 * The initial drawn inside a profile avatar thumbnail.
 *
 * It is a pure projection of the session snapshot handed in, with no memo,
 * cache, or component state of its own: whenever the account's display name
 * changes — a profile save echo, a session refresh, or a different signed-in
 * account — the next render derives the new mark, and every profile avatar in
 * the shell agrees because they all call this one function.
 */
export function profileAvatarInitial(user: ProfileAvatarSource, locale?: Locale | string): string {
  const name = (user?.displayName ?? "").replace(unpairedSurrogate, "").trim();
  if (!name) return profileAvatarFallbackInitial;
  const mark = firstGrapheme(name);
  return mark ? upperCaseMark(mark, locale) : profileAvatarFallbackInitial;
}
