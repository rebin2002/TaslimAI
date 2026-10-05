import type { Locale } from "./i18n";

export const movieDeliveryLocales = [
  "en",
  "ar",
  "ku",
] as const satisfies readonly Locale[];

export const movieDeliveryKeys = [
  "summaryShots",
  "summaryTakes",
  "summarySelected",
  "summaryFinalized",
  "summaryBlockingFindings",
  "summaryMastersReady",
  "inProject",
  "generatedFootage",
  "carriedToMaster",
  "lockedForAssembly",
  "continuityErrors",
  "passedGate",
  "deliveryVerdict",
  "readyForDelivery",
  "notReadyForDelivery",
  "readyExplanation",
  "shotNeedsSelectedTake",
  "blockingFindingsRemain",
  "failedAssembly",
  "noMasterPassed",
  "deliveryGateUnsatisfied",
  "dismissError",
  "productionCheckpoint",
  "checkpointTitle",
  "checkpointText",
  "productionProgress",
  "noCheckpointEvidence",
  "noCheckpointText",
  "state",
  "complete",
  "running",
  "blocked",
  "recoverable",
  "awaitingApproval",
  "sceneShot",
  "continuityReview",
  "findingsWithEvidence",
  "assembledReviewOnly",
  "continuityUnavailable",
  "noContinuityFindings",
  "continuityNoOpenIssue",
  "deliveryGate",
  "outputChecks",
  "qualityGateRecorded",
  "noMasterAssembled",
  "queueFinalAssembly",
  "takeEvidence",
  "approvalStatePerShot",
  "approvalSummary",
  "selectsEvidence",
  "noShotsPlanned",
  "selectedTake",
  "unselectedTake",
  "takesCount",
  "qcFootnote",
  "providerNeutralDelivery",
  "providerNeutralDescription",
  "fastDelivery",
  "masterDelivery",
  "newExport",
  "chooseDeliveryProfile",
  "reviewableTakes",
  "noTakeSelected",
  "selectFinalizeFirst",
  "serverRechecks",
  "deliveryProfile",
  "qualityGate",
  "exportHistory",
  "everyMasterReviewable",
  "completedMastersPrivate",
  "loadingRecords",
  "noExportYet",
  "recordAfterRequest",
  "assemblyProgress",
  "timelineItems",
  "audioInputs",
  "captions",
  "captionModeNone",
  "captionModeEmbedded",
  "captionModeBurnedIn",
  "sources",
  "attempts",
  "completed",
  "masterReady",
  "assemblyStatus",
  "downloadMaster",
  "failedOutput",
  "stillProducing",
  "exportFootnote",
  "exportTerminalStep",
  "assemblyQueued",
  "assemblyQueueError",
] as const;

export type MovieDeliveryKey = (typeof movieDeliveryKeys)[number];
type Catalog = Record<MovieDeliveryKey, string>;

type DeliveryStatus =
  | "Ready"
  | "Failed"
  | "Cancelled"
  | "Queued"
  | "Running"
  | "Pending"
  | "Approved"
  | "Rejected"
  | "ReviewRequired"
  | "Selected"
  | "Draft"
  | "InProgress"
  | "Passed"
  | "Unverified";

const catalogs: Record<Locale, Catalog> = {
  en: {
    summaryShots: "Shots",
    summaryTakes: "Takes",
    summarySelected: "Selected",
    summaryFinalized: "Finalized",
    summaryBlockingFindings: "Blocking findings",
    summaryMastersReady: "Masters ready",
    inProject: "in this project",
    generatedFootage: "generated footage",
    carriedToMaster: "carried to the master",
    lockedForAssembly: "locked for assembly",
    continuityErrors: "continuity errors",
    passedGate: "passed the gate",
    deliveryVerdict: "Delivery verdict",
    readyForDelivery: "Ready for delivery",
    notReadyForDelivery: "Not ready for delivery",
    readyExplanation:
      "Every shot points at a selected take, no blocking continuity finding is open, and a master has passed the quality gate.",
    shotNeedsSelectedTake:
      "{count} shot(s) still have no selected take. Finish the cut in Production.",
    blockingFindingsRemain:
      "{count} blocking continuity finding(s) remain open.",
    failedAssembly:
      "The most recent assembly did not pass the quality gate. Review the sources and export again.",
    noMasterPassed:
      "No master has passed the quality gate yet. Assemble an export in the Exports room.",
    deliveryGateUnsatisfied: "The delivery gate has not been satisfied yet.",
    dismissError: "Dismiss error",
    productionCheckpoint: "Production checkpoint",
    checkpointTitle: "What is actually complete.",
    checkpointText:
      "Derived from persisted shot state and generation jobs—not from optimistic UI state.",
    productionProgress: "Production progress",
    noCheckpointEvidence: "No checkpoint evidence yet",
    noCheckpointText:
      "A checkpoint appears once this project has real shots and generation activity.",
    state: "State",
    complete: "Complete",
    running: "Running",
    blocked: "Blocked",
    recoverable: "Recoverable",
    awaitingApproval: "Awaiting approval",
    sceneShot: "Scene {scene} · Shot {shot}",
    continuityReview: "Continuity review",
    findingsWithEvidence: "Findings with evidence.",
    assembledReviewOnly: "Assembled {time} · review only",
    continuityUnavailable:
      "Continuity review is unavailable for this project right now.",
    noContinuityFindings: "No continuity findings",
    continuityNoOpenIssue:
      "The continuity review did not report an open issue for this project.",
    deliveryGate: "Delivery gate",
    outputChecks: "Output checks on the master.",
    qualityGateRecorded:
      "The quality gate is recorded on each assembly by the encoding job.",
    noMasterAssembled: "No master has been assembled",
    queueFinalAssembly:
      "Queue a final assembly in Exports; its quality result appears here.",
    takeEvidence: "Take evidence",
    approvalStatePerShot: "Approval state per shot.",
    approvalSummary: "{count} take(s) carry an approval decision.",
    selectsEvidence: "Selects evidence lives in the Selects room.",
    noShotsPlanned: "No shots have been planned yet.",
    selectedTake: "Selected",
    unselectedTake: "Unselected",
    takesCount: "{count} take(s)",
    qcFootnote:
      "QC is advisory review data. It never blocks a request silently—the delivery verdict is shown to the customer so the next action stays explicit.",
    providerNeutralDelivery: "Provider-neutral delivery",
    providerNeutralDescription:
      "The export is produced locally from persisted sources. No provider URL, storage key or internal model name is ever exposed to the browser.",
    fastDelivery: "fastest delivery",
    masterDelivery: "master delivery",
    newExport: "New export",
    chooseDeliveryProfile: "Choose a delivery profile.",
    reviewableTakes: "{count} reviewable take(s) are available to assemble.",
    noTakeSelected:
      "No take has been selected yet. Approve and select a take in Production before exporting.",
    selectFinalizeFirst: "Select and finalize a take first.",
    serverRechecks: "The server re-checks every source before encoding.",
    deliveryProfile: "Delivery profile",
    qualityGate: "Quality gate",
    exportHistory: "Export history",
    everyMasterReviewable: "Every master stays reviewable.",
    completedMastersPrivate:
      "Completed masters remain downloadable private assets. Failed attempts keep their diagnostic state.",
    loadingRecords: "Loading export records…",
    noExportYet: "No export yet",
    recordAfterRequest:
      "A record appears here after a real final assembly has been requested for this project.",
    assemblyProgress: "Assembly progress",
    timelineItems: "timeline item(s)",
    audioInputs: "audio input(s)",
    captions: "captions",
    captionModeNone: "none",
    captionModeEmbedded: "embedded",
    captionModeBurnedIn: "burned in",
    sources: "Sources",
    attempts: "Attempts",
    completed: "Completed",
    masterReady: "Master ready",
    assemblyStatus: "Assembly {status}",
    downloadMaster: "Download master",
    failedOutput:
      "This attempt did not publish an output. Re-run the export after correcting the sources.",
    stillProducing:
      "The master is still being produced. Download unlocks when the asset is published.",
    exportFootnote: "Export is the terminal step.",
    exportTerminalStep:
      "A master is only marked Ready after the encoded output passes the deterministic quality gate and the private asset is durably published.",
    assemblyQueued:
      "Final assembly queued. The master is produced from the approved, selected takes already in this project.",
    assemblyQueueError: "The final assembly could not be queued.",
  },
  ar: {
    summaryShots: "اللقطات",
    summaryTakes: "المحاولات",
    summarySelected: "المختارة",
    summaryFinalized: "المثبتة",
    summaryBlockingFindings: "الملاحظات المانعة",
    summaryMastersReady: "النسخ الجاهزة",
    inProject: "في هذا المشروع",
    generatedFootage: "لقطات مولدة",
    carriedToMaster: "منقولة إلى النسخة النهائية",
    lockedForAssembly: "مقفلة للتجميع",
    continuityErrors: "أخطاء الاستمرارية",
    passedGate: "اجتازت البوابة",
    deliveryVerdict: "نتيجة التسليم",
    readyForDelivery: "جاهز للتسليم",
    notReadyForDelivery: "غير جاهز للتسليم",
    readyExplanation:
      "تشير كل لقطة إلى محاولة مختارة، ولا توجد ملاحظة استمرارية مانعة مفتوحة، وقد اجتازت نسخة نهائية بوابة الجودة.",
    shotNeedsSelectedTake:
      "لا تزال {count} لقطة بلا محاولة مختارة. أكمل القص في الإنتاج.",
    blockingFindingsRemain: "لا تزال {count} ملاحظة استمرارية مانعة مفتوحة.",
    failedAssembly:
      "لم يجتز التجميع الأخير بوابة الجودة. راجع المصادر وصدّر من جديد.",
    noMasterPassed:
      "لم تجتز أي نسخة نهائية بوابة الجودة بعد. أنشئ تصديرًا في غرفة التصدير.",
    deliveryGateUnsatisfied: "لم تتحقق بوابة التسليم بعد.",
    dismissError: "إغلاق الخطأ",
    productionCheckpoint: "نقطة فحص الإنتاج",
    checkpointTitle: "ما الذي اكتمل فعليًا؟",
    checkpointText:
      "مشتق من حالة اللقطات المحفوظة ووظائف التوليد، وليس من حالة واجهة متفائلة.",
    productionProgress: "تقدم الإنتاج",
    noCheckpointEvidence: "لا توجد أدلة نقطة فحص بعد",
    noCheckpointText:
      "تظهر نقطة الفحص عندما يحتوي المشروع على لقطات حقيقية ونشاط توليد.",
    state: "الحالة",
    complete: "مكتمل",
    running: "قيد التشغيل",
    blocked: "محظور",
    recoverable: "قابل للاسترداد",
    awaitingApproval: "بانتظار الموافقة",
    sceneShot: "المشهد {scene} · اللقطة {shot}",
    continuityReview: "مراجعة الاستمرارية",
    findingsWithEvidence: "ملاحظات مدعومة بالأدلة.",
    assembledReviewOnly: "تم التجميع في {time} · للمراجعة فقط",
    continuityUnavailable: "مراجعة الاستمرارية غير متاحة لهذا المشروع حاليًا.",
    noContinuityFindings: "لا توجد ملاحظات استمرارية",
    continuityNoOpenIssue:
      "لم تسجل مراجعة الاستمرارية مشكلة مفتوحة لهذا المشروع.",
    deliveryGate: "بوابة التسليم",
    outputChecks: "فحوصات الإخراج على النسخة النهائية.",
    qualityGateRecorded:
      "تُسجل بوابة الجودة على كل تجميع بواسطة وظيفة الترميز.",
    noMasterAssembled: "لم يتم تجميع نسخة نهائية",
    queueFinalAssembly:
      "ضع تجميعًا نهائيًا في قائمة الانتظار من التصدير؛ ستظهر نتيجة الجودة هنا.",
    takeEvidence: "أدلة المحاولات",
    approvalStatePerShot: "حالة الموافقة لكل لقطة.",
    approvalSummary: "تحمل {count} محاولة قرار موافقة.",
    selectsEvidence: "توجد أدلة الاختيارات في غرفة الاختيارات.",
    noShotsPlanned: "لم يتم تخطيط أي لقطات بعد.",
    selectedTake: "مختارة",
    unselectedTake: "لا توجد محاولة مختارة",
    takesCount: "{count} محاولة",
    qcFootnote:
      "بيانات ضبط الجودة للمراجعة الإرشادية. لا تمنع طلبًا بصمت؛ بل تعرض نتيجة التسليم للعميل ليبقى الإجراء التالي واضحًا.",
    providerNeutralDelivery: "تسليم محايد للمزوّد",
    providerNeutralDescription:
      "يُنتج التصدير محليًا من المصادر المحفوظة. لا يُكشف للمتصفح أي عنوان مزوّد أو مفتاح تخزين أو اسم نموذج داخلي.",
    fastDelivery: "أسرع تسليم",
    masterDelivery: "تسليم النسخة النهائية",
    newExport: "تصدير جديد",
    chooseDeliveryProfile: "اختر ملف التسليم.",
    reviewableTakes: "تتوفر {count} محاولة قابلة للمراجعة للتجميع.",
    noTakeSelected:
      "لم يتم اختيار أي محاولة بعد. وافق على محاولة واخترها في الإنتاج قبل التصدير.",
    selectFinalizeFirst: "اختر محاولة وثبتها أولًا.",
    serverRechecks: "يعيد الخادم التحقق من كل مصدر قبل الترميز.",
    deliveryProfile: "ملف التسليم",
    qualityGate: "بوابة الجودة",
    exportHistory: "سجل التصدير",
    everyMasterReviewable: "تبقى كل نسخة نهائية قابلة للمراجعة.",
    completedMastersPrivate:
      "تبقى النسخ النهائية المكتملة أصولًا خاصة قابلة للتنزيل. وتحتفظ المحاولات الفاشلة بحالتها التشخيصية.",
    loadingRecords: "جارٍ تحميل سجلات التصدير…",
    noExportYet: "لا يوجد تصدير بعد",
    recordAfterRequest: "يظهر السجل بعد طلب تجميع نهائي حقيقي لهذا المشروع.",
    assemblyProgress: "تقدم التجميع",
    timelineItems: "عنصرًا زمنيًا",
    audioInputs: "مدخلات صوتية",
    captions: "الترجمة",
    captionModeNone: "بدون ترجمة",
    captionModeEmbedded: "مدمجة",
    captionModeBurnedIn: "مضمّنة في الصورة",
    sources: "المصادر",
    attempts: "المحاولات",
    completed: "المكتمل",
    masterReady: "النسخة النهائية جاهزة",
    assemblyStatus: "التجميع {status}",
    downloadMaster: "تنزيل النسخة النهائية",
    failedOutput:
      "لم تنشر هذه المحاولة إخراجًا. أعد التصدير بعد تصحيح المصادر.",
    stillProducing:
      "لا تزال النسخة النهائية قيد الإنتاج. يتاح التنزيل عند نشر الأصل.",
    exportFootnote: "التصدير هو الخطوة النهائية.",
    exportTerminalStep:
      "لا تُعلّم النسخة النهائية بأنها جاهزة إلا بعد اجتياز الإخراج المشفر بوابة الجودة الحتمية ونشر الأصل الخاص بشكل دائم.",
    assemblyQueued:
      "وُضع التجميع النهائي في قائمة الانتظار. ستُنتج النسخة من المحاولات المعتمدة والمختارة الموجودة في هذا المشروع.",
    assemblyQueueError: "تعذر وضع التجميع النهائي في قائمة الانتظار.",
  },
  ku: {
    summaryShots: "شۆتەکان",
    summaryTakes: "هەوڵەکان",
    summarySelected: "هەڵبژێردراوەکان",
    summaryFinalized: "کۆتایی‌پێهێنراوەکان",
    summaryBlockingFindings: "تێبینییە بەربەستکارەکان",
    summaryMastersReady: "وەشانە ئامادەکان",
    inProject: "لە ئەم پڕۆژەیەدا",
    generatedFootage: "دیمەنی بەرهەمهێنراو",
    carriedToMaster: "گواستراوەتەوە بۆ وەشانی سەرەکی",
    lockedForAssembly: "بۆ کۆکردنەوە قفل کراوە",
    continuityErrors: "هەڵەکانی بەردەوامی",
    passedGate: "دەروازەکەی تێپەڕاندووە",
    deliveryVerdict: "ئەنجامی گەیاندن",
    readyForDelivery: "بۆ گەیاندن ئامادەیە",
    notReadyForDelivery: "بۆ گەیاندن ئامادە نییە",
    readyExplanation:
      "هەر شۆتێک بە هەوڵێکی هەڵبژێردراوەوە بەستراوەتەوە، هیچ تێبینییەکی بەربەستکاری بەردەوامی کراوە نییە، و وەشانێکی سەرەکی دەروازەی کوالێتیی تێپەڕاندووە.",
    shotNeedsSelectedTake:
      "هێشتا {count} شۆت هەڵبژاردەی نییە. کاتەکە لە بەرهەمهێنان تەواو بکە.",
    blockingFindingsRemain:
      "هێشتا {count} تێبینیی بەردەوامیی بەربەستکراو کراوەیە.",
    failedAssembly:
      "کۆکردنەوەی نوێترین نەیتوانی تاقیکردنەوەی کوالێتی تێپەڕێنێت. سەرچاوەکان بپشکنە و دووبارە هەناردە بکە.",
    noMasterPassed:
      "هێشتا هیچ وەشانێکی سەرەکی تاقیکردنەوەی کوالێتی تێنەپەڕاندووە. لە ژووری هەناردەدا هەناردەیەک کۆبکەرەوە.",
    deliveryGateUnsatisfied: "هێشتا دەروازەی گەیاندن پڕ نەکراوەتەوە.",
    dismissError: "داخستنی هەڵە",
    productionCheckpoint: "خاڵی پشکنینی بەرهەمهێنان",
    checkpointTitle: "لە ڕاستیدا چی تەواو بووە؟",
    checkpointText:
      "لە دۆخی تۆمارکراوی شۆتەکان و کارەکانی بەرهەمهێنانەوەیە، نەک لە دۆخی خۆشبینانەی ڕووکار.",
    productionProgress: "پێشکەوتنی بەرهەمهێنان",
    noCheckpointEvidence: "هێشتا هیچ بەڵگەی خاڵی پشکنین نییە",
    noCheckpointText:
      "خاڵی پشکنین کاتێک دەردەکەوێت کە پڕۆژەکە شۆتی ڕاستەقینە و چالاکی بەرهەمهێنانی هەبێت.",
    state: "دۆخ",
    complete: "تەواو",
    running: "لە جێبەجێکردندایە",
    blocked: "بەربەستکراو",
    recoverable: "دەتوانرێت بگەڕێندرێتەوە",
    awaitingApproval: "چاوەڕوانی ڕەزامەندی",
    sceneShot: "دیمەن {scene} · شۆت {shot}",
    continuityReview: "پێداچوونەوەی بەردەوامی",
    findingsWithEvidence: "تێبینییەکان لەگەڵ بەڵگە.",
    assembledReviewOnly: "لە {time} کۆکرایەوە · تەنها بۆ پێداچوونەوە",
    continuityUnavailable:
      "پێداچوونەوەی بەردەوامی بۆ ئەم پڕۆژەیە ئێستا بەردەست نییە.",
    noContinuityFindings: "هیچ تێبینییەکی بەردەوامی نییە",
    continuityNoOpenIssue:
      "پێداچوونەوەی بەردەوامی هیچ کێشەیەکی کراوەی بۆ ئەم پڕۆژەیە تۆمار نەکردووە.",
    deliveryGate: "دەروازەی گەیاندن",
    outputChecks: "پشکنینی دەرچوون لەسەر وەشانی سەرەکی.",
    qualityGateRecorded:
      "دەروازەی کوالێتی لەسەر هەر کۆکردنەوەیەک لەلایەن کاری کۆدکردنەوە تۆمار دەکرێت.",
    noMasterAssembled: "هیچ وەشانێکی سەرەکی کۆنەکراوەتەوە",
    queueFinalAssembly:
      "کۆکردنەوەیەکی کۆتایی لە هەناردەدا بخە ڕیزەوە؛ ئەنجامی کوالێتییەکە لێرە دەردەکەوێت.",
    takeEvidence: "بەڵگەی هەوڵەکان",
    approvalStatePerShot: "دۆخی ڕەزامەندی بۆ هەر شۆتێک.",
    approvalSummary: "{count} هەوڵ بڕیاری ڕەزامەندیی لەگەڵە.",
    selectsEvidence: "بەڵگەی هەڵبژاردەکان لە ژووری هەڵبژاردەکانە.",
    noShotsPlanned: "هێشتا هیچ شۆتێک پلانی بۆ دانراوە.",
    selectedTake: "هەڵبژێردراو",
    unselectedTake: "هیچ هەوڵێکی هەڵنەبژێردراوە",
    takesCount: "{count} هەوڵ",
    qcFootnote:
      "داتای پێداچوونەوەی کۆنترۆڵی کوالێتییە. بەبێ دەنگ داواکاری ناوەستێنێت؛ ئەنجامی گەیاندن بە کڕیار نیشان دەدات تا کردارەکەی داهاتوو ڕوون بێت.",
    providerNeutralDelivery: "گەیاندنی بێلایەنی دابینکەر",
    providerNeutralDescription:
      "هەناردەکە بە شێوەی ناوخۆیی لە سەرچاوە تۆمارکراوەکان دروست دەکرێت. هیچ ناونیشانی دابینکەر، کلیلی هەڵگرتن یان ناوی مۆدێلی ناوخۆیی بۆ وێبگەڕ نادرێت.",
    fastDelivery: "خێراترین گەیاندن",
    masterDelivery: "گەیاندنی وەشانی سەرەکی",
    newExport: "هەناردەی نوێ",
    chooseDeliveryProfile: "پرۆفایلی گەیاندن هەڵبژێرە.",
    reviewableTakes: "{count} هەوڵی بۆ پێداچوونەوە بەردەستە بۆ کۆکردنەوە.",
    noTakeSelected:
      "هێشتا هیچ هەوڵێک هەڵنەبژێردراوە. پێش هەناردەکردن لە بەرهەمهێنان هەوڵێک پەسەند و هەڵبژێرە.",
    selectFinalizeFirst: "سەرەتا هەوڵێک هەڵبژێرە و کۆتایی پێبهێنە.",
    serverRechecks: "ڕاژەکار پێش کۆدکردنەوە هەموو سەرچاوەیەک دووبارە دەپشکنێت.",
    deliveryProfile: "پرۆفایلی گەیاندن",
    qualityGate: "دەروازەی کوالێتی",
    exportHistory: "مێژووی هەناردە",
    everyMasterReviewable: "هەر وەشانێکی سەرەکی هەروا بۆ پێداچوونەوەیە.",
    completedMastersPrivate:
      "وەشانە سەرەکییە تەواوبووەکان سامانە تایبەتە دابەزێنراوەکانن. هەوڵە شکست‌خواردووەکان دۆخی تشخیصەکەیان دەپارێزن.",
    loadingRecords: "تۆمارەکانی هەناردە بار دەکرێن…",
    noExportYet: "هێشتا هەناردە نییە",
    recordAfterRequest:
      "تۆمارەکە دوای داواکاریی کۆکردنەوەی کۆتاییی ڕاستەقینە بۆ ئەم پڕۆژەیە دەردەکەوێت.",
    assemblyProgress: "پێشکەوتنی کۆکردنەوە",
    timelineItems: "دانەی تایمڵاین",
    audioInputs: "هێڵی دەنگ",
    captions: "ژێرنوس",
    captionModeNone: "بێ ژێرنوس",
    captionModeEmbedded: "تێخراوە",
    captionModeBurnedIn: "لە ناو وێنەدا نووسراوە",
    sources: "سەرچاوەکان",
    attempts: "هەوڵەکان",
    completed: "تەواوبوو",
    masterReady: "وەشانی سەرەکی ئامادەیە",
    assemblyStatus: "کۆکردنەوە {status}",
    downloadMaster: "داگرتنی وەشانی سەرەکی",
    failedOutput:
      "ئەم هەوڵە دەرچوونێکی بڵاونەکردەوە. دوای چاککردنی سەرچاوەکان هەناردەکە دووبارە بکە.",
    stillProducing:
      "وەشانی سەرەکی هێشتا دروست دەکرێت. کاتێک سامانەکە بڵاوکرایەوە داگرتن دەکرێت.",
    exportFootnote: "هەناردە هەنگاوی کۆتایییە.",
    exportTerminalStep:
      "وەشانی سەرەکی تەنها کاتێک ئامادە دادەنرێت کە دەرچوونی کۆدکراو دەروازەی کوالێتیی دیاریکراو تێپەڕێنێت و سامانە تایبەتەکە بەردەوام بڵاوکرابێتەوە.",
    assemblyQueued:
      "کۆکردنەوەی کۆتایی خراوەتە ڕیزەوە. وەشانەکە لە هەوڵە پەسەندکراو و هەڵبژێردراوەکانی ئەم پڕۆژەیە دروست دەکرێت.",
    assemblyQueueError: "نەتوانرا کۆکردنەوەی کۆتایی بخرێتە ڕیزەوە.",
  },
};

const statusLabels: Record<Locale, Record<DeliveryStatus, string>> = {
  en: {
    Ready: "Ready",
    Failed: "Failed",
    Cancelled: "Cancelled",
    Queued: "Queued",
    Running: "Running",
    Pending: "Pending",
    Approved: "Approved",
    Rejected: "Rejected",
    ReviewRequired: "Needs review",
    Selected: "Selected",
    Draft: "Draft",
    InProgress: "In progress",
    Passed: "Passed",
    Unverified: "Unverified",
  },
  ar: {
    Ready: "جاهز",
    Failed: "فشل",
    Cancelled: "أُلغي",
    Queued: "في الانتظار",
    Running: "قيد التشغيل",
    Pending: "معلّق",
    Approved: "تمت الموافقة",
    Rejected: "مرفوض",
    ReviewRequired: "يحتاج إلى مراجعة",
    Selected: "مختار",
    Draft: "مسودة",
    InProgress: "قيد التقدم",
    Passed: "ناجح",
    Unverified: "غير موثق",
  },
  ku: {
    Ready: "ئامادە",
    Failed: "شکستی هێنا",
    Cancelled: "هەڵوەشێندرایەوە",
    Queued: "لە ڕیزدایە",
    Running: "لە جێبەجێکردندایە",
    Pending: "چاوەڕوان",
    Approved: "پەسەندکراو",
    Rejected: "ڕەتکراوەتەوە",
    ReviewRequired: "پێویستی بە پێداچوونەوەیە",
    Selected: "هەڵبژێردراو",
    Draft: "ڕەشنووس",
    InProgress: "لە پێشکەوتندایە",
    Passed: "سەرکەوتوو",
    Unverified: "پشتڕاستنەکراوە",
  },
};

const numberLocales: Record<Locale, string> = {
  en: "en-US",
  ar: "ar-u-nu-arab",
  ku: "ku-Arab-u-nu-arab",
};

export function movieDeliveryText(
  locale: Locale,
  key: MovieDeliveryKey,
  variables: Record<string, string | number> = {},
) {
  return catalogs[locale][key].replace(/\{(\w+)\}/g, (_, variable: string) =>
    String(variables[variable] ?? `{${variable}}`),
  );
}

export function movieDeliveryStatusLabel(locale: Locale, value: string) {
  return statusLabels[locale][value as DeliveryStatus] ?? value;
}

export function movieDeliveryCaptionModeLabel(locale: Locale, value: string) {
  if (value === "Embedded")
    return movieDeliveryText(locale, "captionModeEmbedded");
  if (value === "BurnedIn")
    return movieDeliveryText(locale, "captionModeBurnedIn");
  if (value === "None") return movieDeliveryText(locale, "captionModeNone");
  return value;
}

export function formatMovieDeliveryNumber(locale: Locale, value: number) {
  return new Intl.NumberFormat(numberLocales[locale]).format(value);
}

export function formatMovieDeliveryDate(locale: Locale, value: string) {
  return new Intl.DateTimeFormat(numberLocales[locale], {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));
}
