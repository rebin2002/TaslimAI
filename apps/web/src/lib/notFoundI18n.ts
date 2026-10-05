import type { Locale } from "./i18n";

export const notFoundKeys = ["eyebrow", "title", "description", "backHome", "openAssets"] as const;
export type NotFoundKey = (typeof notFoundKeys)[number];

export const notFoundTranslations = {
  en: {
    eyebrow: "Page not found",
    title: "We could not find that page",
    description: "The page may have moved or the address may be incorrect. Return home or open your assets.",
    backHome: "Back to home",
    openAssets: "Open Assets",
  },
  ar: {
    eyebrow: "الصفحة غير موجودة",
    title: "تعذر العثور على هذه الصفحة",
    description: "ربما نُقلت الصفحة أو أن العنوان غير صحيح. عد إلى الرئيسية أو افتح أصولك.",
    backHome: "العودة إلى الرئيسية",
    openAssets: "فتح الأصول",
  },
  ku: {
    eyebrow: "لاپەڕەکە نەدۆزرایەوە",
    title: "نەتوانرا ئەم لاپەڕەیە بدۆزرێتەوە",
    description: "لەوانەیە لاپەڕەکە گوازراوەتەوە یان ناونیشانەکە هەڵە بێت. بگەڕێوە بۆ سەرەکی یان ئاسێتەکانت بکەرەوە.",
    backHome: "گەڕانەوە بۆ سەرەکی",
    openAssets: "ئاسێتەکان بکەرەوە",
  },
} as const satisfies Record<Locale, Record<NotFoundKey, string>>;

export function translateNotFound(locale: Locale, key: NotFoundKey) {
  return notFoundTranslations[locale][key];
}
