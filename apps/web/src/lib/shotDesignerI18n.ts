import type { Locale } from "./i18n";
import {
  CINEMATOGRAPHY_CONTROL_KEYS,
  type CinematographyControlKey,
} from "./movieCinematography";
import { shotDraftLimits, type ShotDraftIssue } from "./shotDraftValidation";

export const shotDesignerLocales = ["en", "ar", "ku"] as const satisfies readonly Locale[];

export const shotDesignerKeys = [
  "title",
  "subtitle",
  "savedToPlan",
  "guideBaseline",
  "noDirection",
  "shotOverrideHint",
  "projectDirection",
  "lockedRevision",
  "simple",
  "advanced",
  "creativeIntent",
  "creativeIntentHint",
  "perShotOverride",
  "productionDirection",
  "shotDescription",
  "shotDescriptionPlaceholder",
  "directionReady",
  "selectIntentHint",
  "capabilityTruth",
  "capabilityTruthHint",
  "tuneDetails",
  "advancedControls",
  "advancedControlsHint",
  "preset",
  "customDirection",
  "continuityConstraints",
  "continuityConstraintsPlaceholder",
  "capabilityTruthExplicit",
  "duration",
  "seconds",
  "guideLockedNote",
  "guideOverrideNote",
  "saving",
  "addShot",
  "savedShots",
  "override",
  "noCinematographyDirection",
  "capabilityClassification",
  "capabilityClassificationHint",
  "capabilityEmpty",
  "durationHint",
  "issueDescriptionRequired",
  "issueDescriptionTooLong",
  "issueDurationNotNumber",
  "issueDurationNotWhole",
  "issueDurationTooSmall",
  "issueDurationTooLarge",
  "issueControlTooLong",
  "issueNotesTooLong",
  "issueContinuityTooMany",
  "issueContinuityLineTooLong",
] as const;

export type ShotDesignerKey = (typeof shotDesignerKeys)[number];
type Catalog = Record<ShotDesignerKey, string>;

const catalogs: Record<Locale, Catalog> = {
  en: {
    title: "Shot Designer",
    subtitle: "Shape the camera without needing to know lenses.",
    savedToPlan: "Saved to shot plan",
    guideBaseline: "Guide baseline",
    noDirection: "No cinematography direction set",
    shotOverrideHint: "This shot can establish its own direction. A shot override never rewrites the Movie Guide.",
    projectDirection: "Project direction",
    lockedRevision: "Locked · rev {revision}",
    simple: "Simple",
    advanced: "Advanced",
    creativeIntent: "Creative intent",
    creativeIntentHint: "Choose a feeling. Taslim fills the production direction.",
    perShotOverride: "Per-shot override",
    productionDirection: "Production direction",
    shotDescription: "Shot description",
    shotDescriptionPlaceholder: "What happens in this shot?",
    directionReady: "{intent} direction ready",
    selectIntentHint: "Select an intent to populate a sensible cinematography direction.",
    capabilityTruth: "Capability truth",
    capabilityTruthHint: "Resolution stays explicit and provider/model names stay out of the creative workflow.",
    tuneDetails: "Tune details",
    advancedControls: "Advanced controls",
    advancedControlsHint: "Every value is a production intent, not a promise of native provider support.",
    preset: "Preset",
    customDirection: "Custom direction",
    continuityConstraints: "Continuity constraints",
    continuityConstraintsPlaceholder: "One locked continuity rule per line",
    capabilityTruthExplicit: "Capability truth stays explicit: {legend}.",
    duration: "Shot duration",
    seconds: "sec",
    guideLockedNote: "Guide stays locked; this saves as a shot-level override.",
    guideOverrideNote: "The Movie Guide remains unchanged; this saves a shot-level override.",
    saving: "Saving…",
    addShot: "Add shot",
    savedShots: "Saved shots",
    override: "override",
    noCinematographyDirection: "No cinematography direction",
    capabilityClassification: "Capability classification",
    capabilityClassificationHint: "Resolution stays explicit and provider/model names stay out of the creative workflow.",
    capabilityEmpty: "This direction has no field-level classification yet. It remains production intent and will not be presented as native support.",
    durationHint: "Whole seconds between {minimum} and {maximum}.",
    issueDescriptionRequired: "Add a shot description so this shot is recognisable later.",
    issueDescriptionTooLong: "Shot description is {characters} characters; the limit is {limit}.",
    issueDurationNotNumber: "Enter the duration as a number of whole seconds, for example 6.",
    issueDurationNotWhole: "Shot duration must be a whole number of seconds.",
    issueDurationTooSmall: "Shot duration must be {minimum} second or longer.",
    issueDurationTooLarge: "Shot duration cannot exceed {limit} seconds.",
    issueControlTooLong: "{field} must be {limit} characters or fewer.",
    issueNotesTooLong: "Direction notes must be {limit} characters or fewer.",
    issueContinuityTooMany: "Continuity constraints accept at most {limit} rules; this draft has {characters}.",
    issueContinuityLineTooLong: "Each continuity constraint must be {limit} characters or fewer.",
  },
  ar: {
    title: "مصمم اللقطات",
    subtitle: "شكّل حركة الكاميرا دون الحاجة إلى معرفة أنواع العدسات.",
    savedToPlan: "تم الحفظ في خطة اللقطات",
    guideBaseline: "المرجع الأساسي للدليل",
    noDirection: "لم يتم تحديد اتجاه سينمائي",
    shotOverrideHint: "يمكن لهذه اللقطة أن تحدد اتجاهها الخاص. لا يغيّر تجاوز اللقطة دليل الفيلم أبدًا.",
    projectDirection: "اتجاه المشروع",
    lockedRevision: "مقفل · المراجعة {revision}",
    simple: "بسيط",
    advanced: "متقدم",
    creativeIntent: "النية الإبداعية",
    creativeIntentHint: "اختر إحساسًا، وسيتولى تسليم ملء اتجاه الإنتاج.",
    perShotOverride: "تجاوز خاص باللقطة",
    productionDirection: "اتجاه الإنتاج",
    shotDescription: "وصف اللقطة",
    shotDescriptionPlaceholder: "ماذا يحدث في هذه اللقطة؟",
    directionReady: "اتجاه {intent} جاهز",
    selectIntentHint: "اختر نية لملء اتجاه سينمائي مناسب.",
    capabilityTruth: "حقيقة الإمكانات",
    capabilityTruthHint: "تبقى الدقة واضحة، وتظل أسماء المزوّدين/النماذج خارج سير العمل الإبداعي.",
    tuneDetails: "ضبط التفاصيل",
    advancedControls: "عناصر تحكم متقدمة",
    advancedControlsHint: "كل قيمة هي نية إنتاج وليست وعدًا بدعم أصلي من المزوّد.",
    preset: "إعداد مسبق",
    customDirection: "اتجاه مخصص",
    continuityConstraints: "قيود الاستمرارية",
    continuityConstraintsPlaceholder: "قاعدة استمرارية مقفلة واحدة في كل سطر",
    capabilityTruthExplicit: "تبقى حقيقة الإمكانات واضحة: {legend}.",
    duration: "مدة اللقطة",
    seconds: "ث",
    guideLockedNote: "يبقى الدليل مقفلًا؛ سيتم حفظ هذا كتجاوز على مستوى اللقطة.",
    guideOverrideNote: "يبقى دليل الفيلم دون تغيير؛ سيتم حفظ هذا كتجاوز على مستوى اللقطة.",
    saving: "جارٍ الحفظ…",
    addShot: "إضافة لقطة",
    savedShots: "اللقطات المحفوظة",
    override: "تجاوز",
    noCinematographyDirection: "لا يوجد اتجاه سينمائي",
    capabilityClassification: "تصنيف الإمكانات",
    capabilityClassificationHint: "تبقى الدقة واضحة، وتظل أسماء المزوّدين/النماذج خارج سير العمل الإبداعي.",
    capabilityEmpty: "لا يملك هذا الاتجاه تصنيفًا على مستوى الحقول بعد. سيظل نية إنتاج ولن يُعرض كدعم أصلي.",
    durationHint: "بالثواني الصحيحة بين {minimum} و{maximum}.",
    issueDescriptionRequired: "أضف وصفًا للقططة حتى تبقى معروفة لاحقًا.",
    issueDescriptionTooLong: "وصف اللقطة {characters} حرفًا؛ الحد الأقصى {limit}.",
    issueDurationNotNumber: "أدخل المدة كعدد من الثواني الصحيحة، مثل ٦.",
    issueDurationNotWhole: "يجب أن تكون مدة اللقطة عددًا صحيحًا من الثواني.",
    issueDurationTooSmall: "يجب أن تكون مدة اللقطة {minimum} ثانية أو أكثر.",
    issueDurationTooLarge: "لا يمكن أن تتجاوز مدة اللقطة {limit} ثانية.",
    issueControlTooLong: "{field} يجب أن يكون {limit} حرفًا أو أقل.",
    issueNotesTooLong: "يجب أن تكون ملاحظات التوجيه {limit} حرفًا أو أقل.",
    issueContinuityTooMany: "لا تقبل قيود الاستمرارية أكثر من {limit} قاعدة؛ تحتوي هذه المسودة على {characters}.",
    issueContinuityLineTooLong: "يجب أن يكون كل قيد استمرارية {limit} حرفًا أو أقل.",
  },
  ku: {
    title: "دیزاینەری شۆت",
    subtitle: "شێوەی کامێراکە دیاری بکە بەبێ ئەوەی پێویستت بە زانینی جۆری لینز هەبێت.",
    savedToPlan: "لە پلانی شۆتدا پاشەکەوت کرا",
    guideBaseline: "بنەمای ڕێبەر",
    noDirection: "هیچ ئاراستەیەکی سینەمایی دیاری نەکراوە",
    shotOverrideHint: "ئەم شۆتە دەتوانێت ئاراستەی خۆی دابنێت. دەستکاریی شۆت هەرگیز ڕێبەری فیلم ناگۆڕێت.",
    projectDirection: "ئاراستەی پڕۆژە",
    lockedRevision: "قفلکراو · پێداچوونەوە {revision}",
    simple: "سادە",
    advanced: "پێشکەوتوو",
    creativeIntent: "مەبەستی داهێنەرانە",
    creativeIntentHint: "هەستێک هەڵبژێرە؛ تسلیم ئاراستەی بەرهەمهێنان پڕ دەکاتەوە.",
    perShotOverride: "دەستکاریی تایبەت بە شۆت",
    productionDirection: "ئاراستەی بەرهەمهێنان",
    shotDescription: "وەسفی شۆت",
    shotDescriptionPlaceholder: "لەو شۆتەدا چی ڕوودەدات؟",
    directionReady: "ئاراستەی {intent} ئامادەیە",
    selectIntentHint: "مەبەستێک هەڵبژێرە بۆ پڕکردنەوەی ئاراستەیەکی سینەمایی گونجاو.",
    capabilityTruth: "ڕاستی تواناکان",
    capabilityTruthHint: "وردەکارییەکان بە ڕوونی دەماونەوە و ناوی دابینکەر/مۆدێل لە ڕێڕەوی داهێنەرانەدا نایەن.",
    tuneDetails: "وردەکارییەکان ڕێکبخە",
    advancedControls: "کۆنترۆڵە پێشکەوتووەکان",
    advancedControlsHint: "هەر بەهایەک مەبەستی بەرهەمهێنانە، نەک بەڵێنێک بۆ پشتگیریی ڕاستەوخۆی دابینکەر.",
    preset: "ڕێکخستنی ئامادە",
    customDirection: "ئاراستەی دەستکرد",
    continuityConstraints: "سنوورەکانی بەردەوامی",
    continuityConstraintsPlaceholder: "لە هەر هێڵێکدا یاسایەکی قفلکراوی بەردەوامی بنووسە",
    capabilityTruthExplicit: "ڕاستی تواناکان بە ڕوونی دەمێنێتەوە: {legend}.",
    duration: "ماوەی شۆت",
    seconds: "چرکە",
    guideLockedNote: "ڕێبەرەکە قفلکراوە؛ ئەمە وەک دەستکاریی ئاستی شۆت پاشەکەوت دەکرێت.",
    guideOverrideNote: "ڕێبەری فیلم ناگۆڕێت؛ ئەمە وەک دەستکاریی ئاستی شۆت پاشەکەوت دەکرێت.",
    saving: "پاشەکەوت دەکرێت…",
    addShot: "زیادکردنی شۆت",
    savedShots: "شۆتە پاشەکەوتکراوەکان",
    override: "دەستکاری",
    noCinematographyDirection: "هیچ ئاراستەیەکی سینەمایی نییە",
    capabilityClassification: "پۆلێنکردنی تواناکان",
    capabilityClassificationHint: "وردەکارییەکان بە ڕوونی دەماونەوە و ناوی دابینکەر/مۆدێل لە ڕێڕەوی داهێنەرانەدا نایەن.",
    capabilityEmpty: "ئەم ئاراستەیە هێشتا پۆلێنکردنی ئاستی خانەی نییە. مەبەستی بەرهەمهێنان دەمێنێتەوە و وەک پشتگیریی ڕاستەوخۆ نیشان نادرێت.",
    durationHint: "بە چرکەی تەواو لەنێو {minimum} بۆ {maximum}.",
    issueDescriptionRequired: "وەسفێک بۆ شۆتەکە زیاد بکە بۆ ئەوەی دواتر بناسرێتەوە.",
    issueDescriptionTooLong: "وەسفی شۆتەکە {characters} پیتە؛ سنوورەکە {limit} پیتە.",
    issueDurationNotNumber: "ماوەکە وەک ژمارەیەک لە چرکەی تەواو بنووسە، بۆ نموونە ٦.",
    issueDurationNotWhole: "ماوەی شۆتەکە دەبێت ژمارەیەکی تەواو بێت لە چرکە.",
    issueDurationTooSmall: "ماوەی شۆتەکە دەبێت {minimum} چرکە یان زیاتر بێت.",
    issueDurationTooLarge: "ماوەی شۆتەکە نابێت لە {limit} چرکە زیاتر بێت.",
    issueControlTooLong: "{field} دەبێت {limit} پیت یان کەمتر بێت.",
    issueNotesTooLong: "تێبینییەکانی ئاراستە دەبێت {limit} پیت یان کەمتر بن.",
    issueContinuityTooMany: "سنوورەکانی بەردەوامی لە زۆرتریندا {limit} ڕێسا وەردەگرێت؛ ئێستا {characters} ڕێسا هەیە.",
    issueContinuityLineTooLong: "هەر سنوورێکی بەردەوامی دەبێت {limit} پیت یان کەمتر بێت.",
  },
};

const controlLabels: Record<Locale, Record<CinematographyControlKey, string>> = {
  en: {
    shotSize: "Shot size",
    focalLength: "Focal length intent",
    lensIntent: "Lens type / intent",
    apertureDepthOfField: "Aperture / depth",
    cameraAngle: "Camera angle",
    cameraMovement: "Camera movement",
    frameRateIntent: "Frame rate",
    lighting: "Lighting",
    exposureLook: "Exposure / look",
    paletteLook: "Palette / look",
    compositionNotes: "Composition",
  },
  ar: {
    shotSize: "حجم اللقطة",
    focalLength: "نية البعد البؤري",
    lensIntent: "نوع العدسة / النية",
    apertureDepthOfField: "الفتحة / العمق",
    cameraAngle: "زاوية الكاميرا",
    cameraMovement: "حركة الكاميرا",
    frameRateIntent: "معدل الإطارات",
    lighting: "الإضاءة",
    exposureLook: "التعريض / المظهر",
    paletteLook: "لوحة الألوان / المظهر",
    compositionNotes: "التكوين",
  },
  ku: {
    shotSize: "قەبارەی شۆت",
    focalLength: "مەبەستی دووریی فۆکەس",
    lensIntent: "جۆری لینز / مەبەست",
    apertureDepthOfField: "دەمچە / قووڵایی",
    cameraAngle: "گۆشەی کامێرا",
    cameraMovement: "جوڵەی کامێرا",
    frameRateIntent: "ڕێژەی فریم",
    lighting: "ڕووناکی",
    exposureLook: "ئاشکراکردن / دیمەن",
    paletteLook: "پالێتی ڕەنگ / دیمەن",
    compositionNotes: "پێکهاتە",
  },
};

const intentLabels: Record<Locale, Record<string, string>> = {
  en: { intimate: "Intimate", natural: "Natural", epic: "Epic", dynamic: "Dynamic" },
  ar: { intimate: "حميمي", natural: "طبيعي", epic: "ملحمي", dynamic: "ديناميكي" },
  ku: { intimate: "نزیکانە", natural: "سروشتی", epic: "ئەپیکی", dynamic: "دینامیکی" },
};

const capabilityLabels: Record<Locale, Record<string, string>> = {
  en: { Native: "Native", Translated: "Translated", "Simulated/Post": "Simulated/Post", Unsupported: "Unsupported" },
  ar: { Native: "أصلي", Translated: "مترجم", "Simulated/Post": "محاكاة/ما بعد الإنتاج", Unsupported: "غير مدعوم" },
  ku: { Native: "ڕاستەوخۆ", Translated: "وەرگێڕدراو", "Simulated/Post": "شبیه‌کراو/دوای بەرهەمهێنان", Unsupported: "پشتگیری نەکراوە" },
};

const numberLocales: Record<Locale, string> = {
  en: "en-US",
  ar: "ar-u-nu-arab",
  ku: "ku-Arab-u-nu-arab",
};

function interpolate(value: string, variables: Record<string, string | number>) {
  return value.replace(/\{(\w+)\}/g, (_, variable: string) => String(variables[variable] ?? `{${variable}}`));
}

export function shotDesignerText(
  locale: Locale,
  key: ShotDesignerKey,
  variables: Record<string, string | number> = {},
) {
  return interpolate(catalogs[locale][key], variables);
}

export function shotDesignerControlLabel(locale: Locale, key: CinematographyControlKey) {
  return controlLabels[locale][key];
}

export function shotDesignerIntentLabel(locale: Locale, intent: string | null | undefined) {
  if (!intent) return shotDesignerText(locale, "noDirection");
  return intentLabels[locale][intent.toLowerCase()] ?? intent;
}

export function shotDesignerCapabilityLabel(locale: Locale, classification: string) {
  return capabilityLabels[locale][classification] ?? classification;
}

export function formatShotDesignerNumber(locale: Locale, value: number) {
  return new Intl.NumberFormat(numberLocales[locale]).format(value);
}

export function formatShotDesignerSequence(locale: Locale, value: number) {
  return new Intl.NumberFormat(numberLocales[locale], {
    minimumIntegerDigits: 2,
    useGrouping: false,
  }).format(value);
}

function isControlField(field: string): field is CinematographyControlKey {
  return (CINEMATOGRAPHY_CONTROL_KEYS as readonly string[]).includes(field);
}

/**
 * Turns one validation issue into the sentence the Shot Designer shows for it.
 * Every message names the bound it violated and the unit it is counted in, so
 * the customer learns what to type rather than that a value was rejected;
 * bounds are quoted in the locale's own digits.
 */
export function shotDraftIssueText(locale: Locale, issue: ShotDraftIssue): string {
  const digits = (value: number) => formatShotDesignerNumber(locale, value);
  if (issue.field === "description") {
    return issue.code === "required"
      ? shotDesignerText(locale, "issueDescriptionRequired")
      : shotDesignerText(locale, "issueDescriptionTooLong", {
          characters: digits(issue.characters ?? 0),
          limit: digits(issue.limit ?? shotDraftLimits.descriptionCharacters),
        });
  }
  if (issue.field === "duration") {
    if (issue.code === "notNumber") return shotDesignerText(locale, "issueDurationNotNumber");
    if (issue.code === "notWhole") return shotDesignerText(locale, "issueDurationNotWhole");
    if (issue.code === "tooSmall") {
      return shotDesignerText(locale, "issueDurationTooSmall", {
        minimum: digits(issue.limit ?? shotDraftLimits.durationSecondsMinimum),
      });
    }
    return shotDesignerText(locale, "issueDurationTooLarge", {
      limit: digits(issue.limit ?? shotDraftLimits.durationSecondsMaximum),
    });
  }
  if (issue.field === "continuityConstraints") {
    return issue.code === "tooManyLines"
      ? shotDesignerText(locale, "issueContinuityTooMany", {
          limit: digits(issue.limit ?? shotDraftLimits.continuityLines),
          characters: digits(issue.characters ?? 0),
        })
      : shotDesignerText(locale, "issueContinuityLineTooLong", {
          limit: digits(issue.limit ?? shotDraftLimits.continuityLineCharacters),
        });
  }
  if (issue.field === "notes") {
    return shotDesignerText(locale, "issueNotesTooLong", {
      limit: digits(issue.limit ?? shotDraftLimits.notesCharacters),
    });
  }
  return shotDesignerText(locale, "issueControlTooLong", {
    field: isControlField(issue.field)
      ? shotDesignerControlLabel(locale, issue.field)
      : issue.field,
    limit: digits(issue.limit ?? shotDraftLimits.controlCharacters),
  });
}
