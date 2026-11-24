import type { Locale } from "./i18n";

export type DevelopmentHubCopy = {
  eyebrow: string;
  description: string;
  navigationLabel: string;
  open: string;
  backHome: string;
  footer: string;
};

type DevelopmentHubCopyField = keyof DevelopmentHubCopy;

const legacyCopy: Record<Locale, DevelopmentHubCopy> = {
  en: {
    eyebrow: "Development workspace",
    description: "Move from learning to shipping with a clear path through the development workspace.",
    navigationLabel: "Development tools",
    open: "Open",
    backHome: "Back to home",
    footer: "Choose one path at a time; your workspace progress stays with the feature you open.",
  },
  ar: {
    eyebrow: "مساحة عمل التطوير",
    description: "انتقل من التعلّم إلى الشحن عبر مسار واضح داخل مساحة التطوير.",
    navigationLabel: "أدوات التطوير",
    open: "فتح",
    backHome: "العودة إلى الرئيسية",
    footer: "اختر مساراً واحداً في كل مرة؛ يبقى تقدّم مساحة العمل مع الميزة التي تفتحها.",
  },
  ku: {
    eyebrow: "شوێنکاریی گەشەپێدان",
    description: "لە فێربوونەوە بۆ ناردن بڕۆ بە ڕێڕەوێکی ڕوون لە شوێنکاریی گەشەپێدان.",
    navigationLabel: "ئامرازەکانی گەشەپێدان",
    open: "کردنەوە",
    backHome: "گەڕانەوە بۆ سەرەکی",
    footer: "لە هەر جارێکدا یەک ڕێڕەو هەڵبژێرە؛ پێشکەوتنی شوێنکار لەگەڵ ئەو تایبەتمەندییە دەمێنێتەوە کە دەیکەیتەوە.",
  },
};

/** Resolve optional pack-owned copy without adding keys to the closed legacy catalog. */
export function resolveDevelopmentHubCopy(locale: Locale, translate: (key: string) => string): DevelopmentHubCopy {
  const fallback = legacyCopy[locale];
  const resolve = (field: DevelopmentHubCopyField) => {
    const key = `developmentHub.${field}`;
    const value = translate(key);
    return value && value !== key ? value : fallback[field];
  };

  return {
    eyebrow: resolve("eyebrow"),
    description: resolve("description"),
    navigationLabel: resolve("navigationLabel"),
    open: resolve("open"),
    backHome: resolve("backHome"),
    footer: resolve("footer"),
  };
}
