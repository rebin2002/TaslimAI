import type { Locale } from "./i18n";
import type { DirectorRoom } from "./movieDirector";

export const movieDirectorLocales = ["en", "ar", "ku"] as const satisfies readonly Locale[];

export const movieDirectorKeys = [
  "ariaLabel",
  "kicker",
  "title",
  "currentRoom",
  "target",
  "lockGuide",
  "lockingGuide",
  "proposal",
  "askPlan",
  "defaultGoal",
  "productionReadyShotPlan",
  "proposedShots",
  "shot",
  "purpose",
  "subjectAction",
  "camera",
  "lightDepth",
  "continuityAudio",
  "propsVfxNotes",
  "none",
  "groundedIn",
  "typedChange",
  "quality",
  "recommendedAuto",
  "selectedQuality",
  "whyPlan",
  "boundary",
  "approveProposal",
  "approving",
  "reject",
  "applyShotPlan",
  "executeReadyAction",
  "applying",
  "regenerateProposal",
  "regenerating",
  "validRoomAction",
  "availablePrerequisites",
  "goal",
  "intelligentMode",
  "autoDirector",
  "autoDirectorHint",
  "qualityAutoActive",
  "chooseQuality",
  "importance",
  "complexity",
  "budgetSensitivity",
  "preparing",
  "createTypedProposal",
  "noValidRoomHelp",
  "reviewableClips",
  "directorHistory",
  "loadingHistory",
  "noEvents",
  "note",
  "errorHistoryUnavailable",
  "errorNoValidAction",
  "errorProposalCreate",
  "errorProposalApprove",
  "errorProposalReject",
  "errorActionExecute",
  "errorProposalRegenerate",
  "errorGuideLock",
  "summaryCast",
  "summaryPlanning",
  "summaryShot",
  "signalPrerequisite",
  "signalWarning",
  "signalApprovalRequired",
  "signalNextAction",
  "signalProductionStatus",
  "signalSuggestion",
  "signalLockGuide",
  "signalApproved",
  "signalSucceeded",
  "signalStoryOverview",
  "signalCast",
  "signalWorld",
  "signalScene",
  "signalShot",
  "signalStoryboard",
  "signalProduction",
] as const;

export type MovieDirectorKey = (typeof movieDirectorKeys)[number];

type Catalog = Record<MovieDirectorKey, string>;

const catalogs: Record<Locale, Catalog> = {
  en: {
    ariaLabel: "Taslim Movie Director", kicker: "Director / Inspector", title: "Taslim Movie Director", currentRoom: "Current room", target: "Target",
    lockGuide: "Lock current guide", lockingGuide: "Locking guide…", proposal: "Proposal", askPlan: "Ask the Director for a plan", defaultGoal: "Prepare the current shot for review",
    productionReadyShotPlan: "Production-ready shot plan", proposedShots: "{count} proposed shots", shot: "Shot {number}", purpose: "Purpose:", subjectAction: "Subject/action:", camera: "Camera:", lightDepth: "Light/depth:", continuityAudio: "Continuity/audio:", propsVfxNotes: "Props/VFX/notes:", none: "None", groundedIn: "Grounded in: {evidence}", typedChange: "Typed change", quality: "Quality", recommendedAuto: "Recommended by Auto Director", selectedQuality: "Selected quality", whyPlan: "Why this plan", boundary: "Review → explicit approval → apply. No silent mutations.",
    approveProposal: "Approve proposal", approving: "Approving…", reject: "Reject", applyShotPlan: "Apply shot plan", executeReadyAction: "Execute ready action", applying: "Applying…", regenerateProposal: "Regenerate proposal", regenerating: "Regenerating…", validRoomAction: "Valid room action", availablePrerequisites: "Available prerequisites", goal: "Goal", intelligentMode: "Intelligent mode", autoDirector: "Auto Director", autoDirectorHint: "Recommends one of the four quality levels.", qualityAutoActive: "Auto Director is active; quality remains Fast / Standard / Cinematic / Studio.", chooseQuality: "Choose a quality level explicitly.", importance: "Importance", complexity: "Complexity", budgetSensitivity: "Budget sensitivity", preparing: "Preparing…", createTypedProposal: "Create typed proposal", noValidRoomHelp: "Open Scenes and select the target needed by this room.", reviewableClips: "{count} reviewable clips", directorHistory: "Director history", loadingHistory: "Loading history…", noEvents: "No Director events yet.", note: "One Director, grounded in this room and target. It never changes production records without your approval.",
    errorHistoryUnavailable: "Director history is unavailable.", errorNoValidAction: "The current room has no valid Director action for this target.", errorProposalCreate: "The Director proposal could not be created.", errorProposalApprove: "The proposal could not be approved.", errorProposalReject: "The proposal could not be rejected.", errorActionExecute: "The Director action could not be executed.", errorProposalRegenerate: "A new Director proposal could not be created.", errorGuideLock: "The Movie Guide could not be locked.", summaryCast: "Turn the current project context into a reviewable Cast continuity plan. No shot is required.", summaryPlanning: "Use the current room, selection, and prerequisites to prepare a reviewable planning result. No media provider will be called.", summaryShot: "Turn the current shot into a reviewable plan. The Director will explain the change, quality choice, and rationale before anything can run.",
    signalPrerequisite: "Prerequisite", signalWarning: "Warning", signalApprovalRequired: "Approval required", signalNextAction: "Next action", signalProductionStatus: "Production status", signalSuggestion: "Suggestion", signalLockGuide: "Lock the current Movie Guide before the Director can assemble authoritative context.", signalApproved: "The proposal is approved. Execute the ready action when you want to queue it.", signalSucceeded: "The Director action completed and its result is recorded in history.", signalStoryOverview: "Lock the guide when the creative rules are ready to travel with the production.", signalCast: "Define the first character so continuity decisions have a durable anchor.", signalWorld: "Define a location before planning shots that depend on a consistent world.", signalScene: "Add a shot plan to this scene when shot-level planning is needed.", signalShot: "Use Auto Director to recommend a quality level from importance, complexity, and continuity sensitivity.", signalStoryboard: "Prepare storyboard context from the shot plan; media generation is not enabled here.", signalProduction: "{percent}% of the current plan has a reviewable clip. The Director is limited to readiness context.",
  },
  ar: {
    ariaLabel: "مخرج تسليم", kicker: "المخرج / المفتش", title: "مخرج تسليم", currentRoom: "الغرفة الحالية", target: "الهدف",
    lockGuide: "قفل الدليل الحالي", lockingGuide: "جارٍ قفل الدليل…", proposal: "المقترح", askPlan: "اطلب من المخرج خطة", defaultGoal: "حضّر اللقطة الحالية للمراجعة",
    productionReadyShotPlan: "خطة لقطات جاهزة للإنتاج", proposedShots: "{count} لقطات مقترحة", shot: "اللقطة {number}", purpose: "الغرض:", subjectAction: "الموضوع/الفعل:", camera: "الكاميرا:", lightDepth: "الإضاءة/العمق:", continuityAudio: "الاستمرارية/الصوت:", propsVfxNotes: "الإكسسوارات/المؤثرات/الملاحظات:", none: "لا شيء", groundedIn: "مبني على: {evidence}", typedChange: "تغيير محدد", quality: "الجودة", recommendedAuto: "موصى به من المخرج التلقائي", selectedQuality: "الجودة المحددة", whyPlan: "سبب الخطة", boundary: "مراجعة ← موافقة صريحة ← تطبيق. لا تغييرات صامتة.",
    approveProposal: "الموافقة على المقترح", approving: "جارٍ اعتماد المقترح…", reject: "رفض", applyShotPlan: "تطبيق خطة اللقطات", executeReadyAction: "تنفيذ الإجراء الجاهز", applying: "جارٍ التطبيق…", regenerateProposal: "إعادة إنشاء المقترح", regenerating: "جارٍ إعادة الإنشاء…", validRoomAction: "إجراء الغرفة المتاح", availablePrerequisites: "المتطلبات المتاحة", goal: "الهدف", intelligentMode: "الوضع الذكي", autoDirector: "المخرج التلقائي", autoDirectorHint: "يوصي بأحد مستويات الجودة الأربعة.", qualityAutoActive: "المخرج التلقائي نشط؛ الجودة تبقى سريع / قياسي / سينمائي / استوديو.", chooseQuality: "اختر مستوى جودة صراحةً.", importance: "الأهمية", complexity: "التعقيد", budgetSensitivity: "حساسية الميزانية", preparing: "جارٍ التحضير…", createTypedProposal: "إنشاء مقترح محدد", noValidRoomHelp: "افتح المشاهد واختر الهدف المطلوب لهذه الغرفة.", reviewableClips: "{count} مقاطع قابلة للمراجعة", directorHistory: "سجل المخرج", loadingHistory: "جارٍ تحميل السجل…", noEvents: "لا توجد أحداث للمخرج بعد.", note: "مخرج واحد يستند إلى هذه الغرفة والهدف. لا يغيّر سجلات الإنتاج دون موافقتك.",
    errorHistoryUnavailable: "سجل المخرج غير متاح.", errorNoValidAction: "لا يوجد إجراء صالح للمخرج في الغرفة الحالية لهذا الهدف.", errorProposalCreate: "تعذر إنشاء مقترح المخرج.", errorProposalApprove: "تعذر اعتماد المقترح.", errorProposalReject: "تعذر رفض المقترح.", errorActionExecute: "تعذر تنفيذ إجراء المخرج.", errorProposalRegenerate: "تعذر إنشاء مقترح مخرج جديد.", errorGuideLock: "تعذر قفل دليل الفيلم.", summaryCast: "حوّل سياق المشروع الحالي إلى خطة استمرارية قابلة للمراجعة لفريق التمثيل. لا حاجة إلى لقطة.", summaryPlanning: "استخدم الغرفة الحالية والاختيار والمتطلبات لإعداد نتيجة تخطيط قابلة للمراجعة. لن يتم استدعاء أي مزود وسائط.", summaryShot: "حوّل اللقطة الحالية إلى خطة قابلة للمراجعة. سيشرح المخرج التغيير واختيار الجودة والسبب قبل تشغيل أي شيء.",
    signalPrerequisite: "متطلب", signalWarning: "تحذير", signalApprovalRequired: "الموافقة مطلوبة", signalNextAction: "الإجراء التالي", signalProductionStatus: "حالة الإنتاج", signalSuggestion: "اقتراح", signalLockGuide: "اقفل دليل الفيلم الحالي قبل أن يجمع المخرج سياقًا موثوقًا.", signalApproved: "تم اعتماد المقترح. نفّذ الإجراء الجاهز عندما تريد وضعه في قائمة الانتظار.", signalSucceeded: "اكتمل إجراء المخرج وسُجلت نتيجته في السجل.", signalStoryOverview: "اقفل الدليل عندما تصبح القواعد الإبداعية جاهزة لمرافقة الإنتاج.", signalCast: "عرّف الشخصية الأولى حتى تستند قرارات الاستمرارية إلى مرجع دائم.", signalWorld: "عرّف موقعًا قبل تخطيط لقطات تعتمد على عالم متسق.", signalScene: "أضف خطة لقطات إلى هذا المشهد عندما تحتاج إلى تخطيط على مستوى اللقطة.", signalShot: "استخدم المخرج التلقائي لاقتراح مستوى جودة بناءً على الأهمية والتعقيد وحساسية الاستمرارية.", signalStoryboard: "حضّر سياق لوحة القصة من خطة اللقطات؛ إنشاء الوسائط غير مفعّل هنا.", signalProduction: "{percent}% من الخطة الحالية لديها مقطع قابل للمراجعة. يقتصر المخرج على سياق الجاهزية.",
  },
  ku: {
    ariaLabel: "بەڕێوەبەری فیلمی Taslim", kicker: "بەڕێوەبەر / پشکنەر", title: "بەڕێوەبەری فیلمی Taslim", currentRoom: "ژووری ئێستا", target: "ئامانج",
    lockGuide: "ڕێبەری ئێستا قفل بکە", lockingGuide: "ڕێبەرەکە قفل دەکرێت…", proposal: "پێشنیار", askPlan: "لە بەڕێوەبەرەکە پلانێک داوا بکە", defaultGoal: "شۆتی ئێستا بۆ پێداچوونەوە ئامادە بکە",
    productionReadyShotPlan: "پلانی شۆتی ئامادەی بەرهەمهێنان", proposedShots: "{count} شۆتی پێشنیارکراو", shot: "شۆتی {number}", purpose: "مەبەست:", subjectAction: "بابەت/کردار:", camera: "کامێرا:", lightDepth: "ڕووناکی/قووڵایی:", continuityAudio: "بەردەوامی/دەنگ:", propsVfxNotes: "کەل‌وپەل/VFX/تێبینی:", none: "هیچ", groundedIn: "پشتبەستووە بە: {evidence}", typedChange: "گۆڕینی دیاریکراو", quality: "کوالێتی", recommendedAuto: "لە لایەن بەڕێوەبەری خۆکارەوە پێشنیارکراوە", selectedQuality: "کوالێتی هەڵبژێردراو", whyPlan: "هۆکاری ئەم پلانە", boundary: "پێداچوونەوە ← ڕەزامەندیی ڕوون ← جێبەجێکردن. هیچ گۆڕانکارییەکی بێدەنگ نییە.",
    approveProposal: "پەسەندکردنی پێشنیار", approving: "پێشنیارەکە پەسەند دەکرێت…", reject: "ڕەتکردنەوە", applyShotPlan: "پلانی شۆت جێبەجێ بکە", executeReadyAction: "کرداری ئامادە جێبەجێ بکە", applying: "جێبەجێ دەکرێت…", regenerateProposal: "دروستکردنەوەی پێشنیار", regenerating: "پێشنیارەکە دووبارە دروست دەکرێت…", validRoomAction: "کرداری بەردەستی ژوور", availablePrerequisites: "پێداویستییە بەردەستەکان", goal: "ئامانج", intelligentMode: "دۆخی زیرەک", autoDirector: "بەڕێوەبەری خۆکار", autoDirectorHint: "یەکێک لە چوار ئاستی کوالێتی پێشنیار دەکات.", qualityAutoActive: "بەڕێوەبەری خۆکار چالاکە؛ کوالێتی هەروا خێرا / ستاندارد / سینەمایی / ستۆدیۆیە.", chooseQuality: "ئاستێکی کوالێتی بە ڕوونی هەڵبژێرە.", importance: "گرنگی", complexity: "ئاڵۆزی", budgetSensitivity: "هەستیاری بودجە", preparing: "ئامادە دەکرێت…", createTypedProposal: "پێشنیاری دیاریکراو دروست بکە", noValidRoomHelp: "دیمەنەکان بکەرەوە و ئامانجی پێویست بۆ ئەم ژوورە هەڵبژێرە.", reviewableClips: "{count} کلیپی بۆ پێداچوونەوە", directorHistory: "مێژووی بەڕێوەبەر", loadingHistory: "مێژوو بار دەکرێت…", noEvents: "هێشتا هیچ ڕووداوێکی بەڕێوەبەر نییە.", note: "یەک بەڕێوەبەر، پشتبەستوو بە ئەم ژوور و ئامانجە. بەبێ ڕەزامەندیی تۆ هیچ تۆمارێکی بەرهەمهێنان ناگۆڕێت.",
    errorHistoryUnavailable: "مێژووی بەڕێوەبەر بەردەست نییە.", errorNoValidAction: "لە ژووری ئێستا بۆ ئەم ئامانجە هیچ کرداری دروستی بەڕێوەبەر نییە.", errorProposalCreate: "پێشنیاری بەڕێوەبەر دروست نەکرا.", errorProposalApprove: "پێشنیارەکە پەسەند نەکرا.", errorProposalReject: "پێشنیارەکە ڕەت نەکرایەوە.", errorActionExecute: "کرداری بەڕێوەبەر جێبەجێ نەکرا.", errorProposalRegenerate: "پێشنیاری نوێی بەڕێوەبەر دروست نەکرا.", errorGuideLock: "ڕێبەری فیلم قفل نەکرا.", summaryCast: "کۆنتێکستی پڕۆژەی ئێستا بکە بە پلانێکی بەردەوامی کاراکتەرەکان کە پێداچوونەوەی بۆ بکرێت. هیچ شۆتێک پێویست نییە.", summaryPlanning: "ژوور، هەڵبژاردن و پێداویستییەکانی ئێستا بەکاربهێنە بۆ ئەنجامێکی پلاندانانی پێداچوونەوەکراو. هیچ دابینکەرێکی میدیا بانگ ناکرێت.", summaryShot: "شۆتی ئێستا بکە بە پلانێکی پێداچوونەوەکراو. بەڕێوەبەرەکە پێش جێبەجێکردن گۆڕان، هەڵبژاردنی کوالێتی و هۆکارەکە ڕوون دەکاتەوە.",
    signalPrerequisite: "پێداویستی", signalWarning: "ئاگاداری", signalApprovalRequired: "ڕەزامەندی پێویستە", signalNextAction: "کرداری داهاتوو", signalProductionStatus: "دۆخی بەرهەمهێنان", signalSuggestion: "پێشنیار", signalLockGuide: "پێش ئەوەی بەڕێوەبەر کۆنتێکستی پشتڕاست کۆبکاتەوە، ڕێبەری فیلمی ئێستا قفل بکە.", signalApproved: "پێشنیارەکە پەسەند کرا. کاتێک دەتەوێت بیخەیتە ڕیزی کار، کرداری ئامادە جێبەجێ بکە.", signalSucceeded: "کرداری بەڕێوەبەر تەواو بوو و ئەنجامەکەی لە مێژوودا تۆمار کرا.", signalStoryOverview: "کاتێک یاسا داهێنەرانەکان ئامادەی بەرهەمهێنان بوون، ڕێبەرەکە قفل بکە.", signalCast: "یەکەم کاراکتەر دیاری بکە تا بڕیارەکانی بەردەوامی پشتبەستەیەکی هەمیشەیی هەبێت.", signalWorld: "پێش پلاندانی شۆتەکان شوێنێک دیاری بکە کە جیهانێکی یەکگرتوو پێویستە.", signalScene: "کاتێک پلاندانی ئاستی شۆت پێویستە، پلانی شۆت بۆ ئەم دیمەنە زیاد بکە.", signalShot: "بەڕێوەبەری خۆکار بەکاربهێنە بۆ پێشنیارکردنی کوالێتی لەسەر بنەمای گرنگی، ئاڵۆزی و هەستیاری بەردەوامی.", signalStoryboard: "کۆنتێکستی تەختەی چیرۆک لە پلانی شۆت ئامادە بکە؛ دروستکردنی میدیا لێرە چالاک نییە.", signalProduction: "{percent}% لە پلانی ئێستا کلیپی بۆ پێداچوونەوەی هەیە. بەڕێوەبەر تەنها کۆنتێکستی ئامادەیی بەڕێوە دەبات.",
  },
};

const roomLabels: Record<Locale, Record<DirectorRoom, string>> = {
  en: { Overview: "Overview", Story: "Story", Cast: "Cast", World: "World", Scene: "Scene", Shot: "Shot", Storyboard: "Storyboard", Production: "Production" },
  ar: { Overview: "نظرة عامة", Story: "القصة", Cast: "فريق التمثيل", World: "العالم", Scene: "المشهد", Shot: "اللقطة", Storyboard: "لوحة القصة", Production: "الإنتاج" },
  ku: { Overview: "پوختە", Story: "چیرۆک", Cast: "کاراکتەرەکان", World: "جیهان", Scene: "دیمەن", Shot: "شۆت", Storyboard: "تەختەی چیرۆک", Production: "بەرهەمهێنان" },
};

const roomDetails: Record<Locale, Record<string, string>> = {
  en: { overview: "project overview and readiness", story: "creative brief and continuity guide", cast: "{count} character records", world: "{count} location records", sceneShot: "shot plan inside the selected scene", sceneSelected: "{count} shots planned", noScene: "No scene selected", chooseScene: "Choose a scene from the production map", noShot: "No shot selected", chooseShot: "Choose or add a shot in the Scene room", storyboardShot: "storyboard preparation context", storyboardScene: "storyboard candidates and visual continuity", storyboardPass: "scene-level visual plan", production: "production readiness" },
  ar: { overview: "نظرة عامة على المشروع وجاهزيته", story: "الموجز الإبداعي ودليل الاستمرارية", cast: "{count} سجلات شخصيات", world: "{count} سجلات مواقع", sceneShot: "خطة اللقطات داخل المشهد المحدد", sceneSelected: "تم التخطيط لـ {count} لقطات", noScene: "لم يتم تحديد مشهد", chooseScene: "اختر مشهدًا من خريطة الإنتاج", noShot: "لم يتم تحديد لقطة", chooseShot: "اختر لقطة أو أضفها في غرفة المشهد", storyboardShot: "سياق إعداد لوحة القصة", storyboardScene: "مرشحات لوحة القصة والاستمرارية المرئية", storyboardPass: "خطة مرئية على مستوى المشهد", production: "جاهزية الإنتاج" },
  ku: { overview: "پوختەی پڕۆژە و ئامادەیی", story: "پوختەی داهێنەرانە و ڕێبەری بەردەوامی", cast: "{count} تۆماری کاراکتەر", world: "{count} تۆماری شوێن", sceneShot: "پلانی شۆت لە ناو دیمەنی هەڵبژێردراو", sceneSelected: "{count} شۆت پلانی بۆ دانراوە", noScene: "هیچ دیمەنێک هەڵنەبژێردراوە", chooseScene: "دیمەنێک لە نەخشەی بەرهەمهێنان هەڵبژێرە", noShot: "هیچ شۆتێک هەڵنەبژێردراوە", chooseShot: "لە ژووری دیمەن شۆتێک هەڵبژێرە یان زیاد بکە", storyboardShot: "کۆنتێکستی ئامادەکردنی تەختەی چیرۆک", storyboardScene: "کاندیدەکانی تەختەی چیرۆک و بەردەوامیی بینراو", storyboardPass: "پلانی بینراوی ئاستی دیمەن", production: "ئامادەیی بەرهەمهێنان" },
};

const actionLabels: Record<Locale, Record<string, string>> = {
  en: { generate_shot: "Generate shot", propose_shots: "Apply shot plan", regenerate_shots: "Apply regenerated shot plan", story_assistance: "Cast continuity assistance", scene_planning: "Plan scene", shot_planning: "Plan shot", storyboard_preparation: "Prepare storyboard context", production_readiness: "Review production readiness", project_readiness: "Review project readiness", edit_repair_audio: "Plan audio bridges", default: "Director action" },
  ar: { generate_shot: "إنشاء لقطة", propose_shots: "تطبيق خطة اللقطات", regenerate_shots: "تطبيق خطة اللقطات المعاد إنشاؤها", story_assistance: "مساعدة استمرارية فريق التمثيل", scene_planning: "تخطيط المشهد", shot_planning: "تخطيط اللقطة", storyboard_preparation: "إعداد سياق لوحة القصة", production_readiness: "مراجعة جاهزية الإنتاج", project_readiness: "مراجعة جاهزية المشروع", edit_repair_audio: "تخطيط جسور صوتية", default: "إجراء المخرج" },
  ku: { generate_shot: "دروستکردنی شۆت", propose_shots: "جێبەجێکردنی پلانی شۆت", regenerate_shots: "جێبەجێکردنی پلانی دووبارەدروستکراوی شۆت", story_assistance: "یارمەتی بەردەوامی کاراکتەر", scene_planning: "پلاندانی دیمەن", shot_planning: "پلاندانی شۆت", storyboard_preparation: "ئامادەکردنی کۆنتێکستی تەختەی چیرۆک", production_readiness: "پشکنینی ئامادەیی بەرهەمهێنان", project_readiness: "پشکنینی ئامادەیی پڕۆژە", edit_repair_audio: "پلاندانی پردە دەنگییەکان", default: "کرداری بەڕێوەبەر" },
};

const statusLabels: Record<Locale, Record<string, string>> = {
  en: { PendingApproval: "Review needed", Approved: "Approved · ready to run", Rejected: "Rejected", Expired: "Expired" },
  ar: { PendingApproval: "تحتاج إلى مراجعة", Approved: "تمت الموافقة · جاهز للتشغيل", Rejected: "مرفوض", Expired: "منتهي" },
  ku: { PendingApproval: "پێویستی بە پێداچوونەوەیە", Approved: "پەسەندکراو · ئامادەی جێبەجێکردن", Rejected: "ڕەتکراوەتەوە", Expired: "بەسەرچووە" },
};

const historyLabels: Record<Locale, Record<string, string>> = {
  en: { context_assembled: "Context assembled", proposal_created: "Proposal created", proposal_approved: "Proposal approved", proposal_rejected: "Proposal rejected", action_ready: "Action ready", action_started: "Action started", action_succeeded: "Action completed", action_failed: "Action needs attention", default: "Director update" },
  ar: { context_assembled: "تم تجميع السياق", proposal_created: "تم إنشاء المقترح", proposal_approved: "تم اعتماد المقترح", proposal_rejected: "تم رفض المقترح", action_ready: "الإجراء جاهز", action_started: "بدأ الإجراء", action_succeeded: "اكتمل الإجراء", action_failed: "الإجراء يحتاج إلى انتباه", default: "تحديث المخرج" },
  ku: { context_assembled: "کۆنتێکست کۆکرایەوە", proposal_created: "پێشنیار دروست کرا", proposal_approved: "پێشنیار پەسەند کرا", proposal_rejected: "پێشنیار ڕەتکرایەوە", action_ready: "کردارەکە ئامادەیە", action_started: "کردارەکە دەستی پێکرد", action_succeeded: "کردارەکە تەواو بوو", action_failed: "کردارەکە پێویستی بە سەرنجە", default: "نوێکردنەوەی بەڕێوەبەر" },
};

const reasonLabels: Record<Locale, Record<string, string>> = {
  en: { high_story_importance: "High story importance", lower_story_importance: "Lower story importance", high_creative_complexity: "High creative complexity", bounded_creative_complexity: "Bounded creative complexity", budget_sensitive: "Budget sensitive", budget_flexible: "Budget flexible", budget_limit_selected_lower_quality: "Budget limit selected a lower quality", unresolved_audio_needs_present: "Unresolved audio needs present", canonical_timeline_version_checked: "Canonical timeline version checked", existing_approved_audio_only: "Existing approved audio only", bridge_ranges_are_advisory: "Bridge ranges are advisory", explicit_user_override_required: "Explicit user override required" },
  ar: { high_story_importance: "أهمية قصصية عالية", lower_story_importance: "أهمية قصصية أقل", high_creative_complexity: "تعقيد إبداعي عالٍ", bounded_creative_complexity: "تعقيد إبداعي محدود", budget_sensitive: "حساس للميزانية", budget_flexible: "مرن من ناحية الميزانية", budget_limit_selected_lower_quality: "اختار حد الميزانية جودة أقل", unresolved_audio_needs_present: "توجد احتياجات صوتية غير محلولة", canonical_timeline_version_checked: "تم التحقق من إصدار الخط الزمني الأساسي", existing_approved_audio_only: "الصوت المعتمد الموجود فقط", bridge_ranges_are_advisory: "نطاقات الجسور إرشادية", explicit_user_override_required: "مطلوب تجاوز صريح من المستخدم" },
  ku: { high_story_importance: "گرنگیی بەرزی چیرۆک", lower_story_importance: "گرنگیی کەمتر بۆ چیرۆک", high_creative_complexity: "ئاڵۆزیی داهێنەرانەی بەرز", bounded_creative_complexity: "ئاڵۆزیی داهێنەرانەی سنووردار", budget_sensitive: "هەستیار بە بودجە", budget_flexible: "لە ڕووی بودجەوە نەرمە", budget_limit_selected_lower_quality: "سنووری بودجە کوالێتییەکی نزمتر هەڵبژارد", unresolved_audio_needs_present: "پێداویستیی دەنگی چارەسەرنەکراو هەیە", canonical_timeline_version_checked: "وەشانی تایمڵاینی سەرەکی پشکنرا", existing_approved_audio_only: "تەنها دەنگی پەسەندکراوی هەنووکەیی", bridge_ranges_are_advisory: "مەوداکانی پردەکان ڕێنمایین", explicit_user_override_required: "پێویستی بە دەستێوەردانی ڕوونی بەکارهێنەرە" },
};

const prerequisiteLabels: Record<Locale, Record<string, [string, string, string, string]>> = {
  en: { guide_locked: ["Movie Guide locked", "Authoritative guide rules are available.", "Lock the current Movie Guide first.", "The Movie Guide is locked."] , scene_selected: ["Scene selected", "The Director is scoped to the selected scene.", "Select a scene from the production map.", "The scene is selected."], shot_selected: ["Shot selected", "The Director can target this shot.", "Select a shot when the room action is shot-specific.", "The shot is selected."], shot_plan_ready: ["Shot plan ready", "The selected shot has a usable plan.", "Add the missing shot-plan detail before asking for shot planning.", "The shot plan is ready."] },
  ar: { guide_locked: ["تم قفل دليل الفيلم", "قواعد الدليل المعتمدة متاحة.", "اقفل دليل الفيلم الحالي أولًا.", "دليل الفيلم مقفل."], scene_selected: ["تم تحديد المشهد", "المخرج محدد بالمشهد المختار.", "اختر مشهدًا من خريطة الإنتاج.", "تم تحديد المشهد."], shot_selected: ["تم تحديد اللقطة", "يمكن للمخرج استهداف هذه اللقطة.", "اختر لقطة عندما يكون إجراء الغرفة خاصًا باللقطة.", "تم تحديد اللقطة."], shot_plan_ready: ["خطة اللقطة جاهزة", "لدى اللقطة المحددة خطة قابلة للاستخدام.", "أضف تفاصيل خطة اللقطة الناقصة قبل طلب تخطيطها.", "خطة اللقطة جاهزة."] },
  ku: { guide_locked: ["ڕێبەری فیلم قفل کراوە", "یاساکانی ڕێبەری پشتڕاست بەردەستن.", "سەرەتا ڕێبەری فیلمی ئێستا قفل بکە.", "ڕێبەری فیلم قفل کراوە."], scene_selected: ["دیمەن هەڵبژێردراوە", "بەڕێوەبەرەکە بۆ دیمەنی هەڵبژێردراو سنووردارە.", "دیمەنێک لە نەخشەی بەرهەمهێنان هەڵبژێرە.", "دیمەنەکە هەڵبژێردراوە."], shot_selected: ["شۆت هەڵبژێردراوە", "بەڕێوەبەرەکە دەتوانێت ئەم شۆتە ئامانج بگرێت.", "کاتێک کرداری ژوور تایبەتە بە شۆتەکە، شۆتێک هەڵبژێرە.", "شۆتەکە هەڵبژێردراوە."], shot_plan_ready: ["پلانی شۆت ئامادەیە", "شۆتی هەڵبژێردراو پلانێکی بەکاربهێنانی هەیە.", "پێش داواکاریی پلاندانی شۆت، وردەکارییە کەمەکە زیاد بکە.", "پلانی شۆت ئامادەیە."] },
};

const numberLocales: Record<Locale, string> = { en: "en-US", ar: "ar-u-nu-arab", ku: "ku-Arab-u-nu-arab" };

export function movieDirectorText(locale: Locale, key: MovieDirectorKey, variables: Record<string, string | number> = {}) {
  return catalogs[locale][key].replace(/\{(\w+)\}/g, (_, variable: string) => String(variables[variable] ?? `{${variable}}`));
}

export function movieDirectorRoomLabel(locale: Locale, room: DirectorRoom) {
  return roomLabels[locale][room];
}

export function movieDirectorRoomDetail(locale: Locale, key: string, variables: Record<string, string | number> = {}) {
  const value = roomDetails[locale][key] ?? roomDetails.en[key];
  return value.replace(/\{(\w+)\}/g, (_, variable: string) => String(variables[variable] ?? `{${variable}}`));
}

export function movieDirectorActionLabel(locale: Locale, actionType: string) {
  return actionLabels[locale][actionType] ?? actionLabels[locale].default;
}

export function movieDirectorProposalStatusLabel(locale: Locale, status: string) {
  return statusLabels[locale][status] ?? status;
}

export function movieDirectorHistoryLabel(locale: Locale, eventType: string) {
  return historyLabels[locale][eventType] ?? historyLabels[locale].default;
}

export function movieDirectorReasonLabel(locale: Locale, reason: string) {
  return reasonLabels[locale][reason] ?? reason.replaceAll("_", " ");
}

export function movieDirectorPrerequisite(locale: Locale, key: string, satisfied: boolean) {
  const values = prerequisiteLabels[locale][key] ?? prerequisiteLabels.en[key];
  return { label: values[0], detail: satisfied ? values[1] : values[2] };
}

export function formatMovieDirectorNumber(locale: Locale, value: number) {
  return new Intl.NumberFormat(numberLocales[locale]).format(value);
}

export function formatMovieDirectorDate(locale: Locale, value: string) {
  return new Intl.DateTimeFormat(numberLocales[locale], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(new Date(value));
}

export function movieDirectorQualityLabel(locale: Locale, value: string) {
  const labels: Record<Locale, Record<string, string>> = {
    en: { Fast: "Fast", Standard: "Standard", Cinematic: "Cinematic", Studio: "Studio" },
    ar: { Fast: "سريع", Standard: "قياسي", Cinematic: "سينمائي", Studio: "استوديو" },
    ku: { Fast: "خێرا", Standard: "ستاندارد", Cinematic: "سینەمایی", Studio: "ستۆدیۆ" },
  };
  return labels[locale][value] ?? value;
}
