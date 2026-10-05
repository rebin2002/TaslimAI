"use client";

import Link from "next/link";
import { ArrowLeft, Check, CheckCircle2, ClipboardCheck, Code2, LockKeyhole, RefreshCw, ShieldCheck } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import type { Locale } from "@/lib/i18n";
import {
  createDefaultDevelopmentBuildState,
  isDevelopmentBuildStepComplete,
  readDevelopmentBuildState,
  saveDevelopmentBuildState,
  setDevelopmentBuildNote,
  setDevelopmentBuildStepCompletion,
  type DevelopmentBuildState,
} from "@/lib/developmentBuildState";
import styles from "./DevelopmentBuildChecklist.module.css";

type BuildStep = { id: string; title: string; description: string };
type BuildCopy = {
  eyebrow: string;
  title: string;
  description: string;
  workspaceNote: string;
  checklistLabel: string;
  stepsLabel: string;
  progress: string;
  complete: string;
  markIncomplete: string;
  notesLabel: string;
  notesPlaceholder: string;
  notesHint: string;
  saved: string;
  storageError: string;
  loading: string;
  noWorkspace: string;
  home: string;
  guide: string;
  steps: BuildStep[];
};

const copy: Record<Locale, BuildCopy> = {
  en: {
    eyebrow: "Build readiness",
    title: "Turn a good idea into a safe change",
    description: "Use this short gate before opening a pull request. It keeps the next development step focused, tested, and easy to revisit.",
    workspaceNote: "Your checklist and note are saved only in this workspace on this device.",
    checklistLabel: "Build readiness checklist",
    stepsLabel: "Readiness steps",
    progress: "{done} of {total} checks complete",
    complete: "Complete",
    markIncomplete: "Mark incomplete",
    notesLabel: "Build note",
    notesPlaceholder: "Record the scope, test command, or follow-up you want to remember…",
    notesHint: "This note is limited to 2,000 characters and never leaves this browser. Redact secrets, tokens, credentials, and personal data before writing.",
    saved: "Checklist saved",
    storageError: "This device could not save the checklist. You can continue, but revisit it before switching browsers.",
    loading: "Loading your build checklist…",
    noWorkspace: "This checklist is unavailable until a workspace is ready.",
    home: "Back to home",
    guide: "Back to learning guide",
    steps: [
      { id: "scope", title: "Name one outcome", description: "State the user-visible result and keep the first change small enough to review in one pass." },
      { id: "boundary", title: "Confirm the boundary", description: "Identify the workspace, account, and permission boundary this change must respect before editing." },
      { id: "contract", title: "Write the contract test", description: "Add or update a focused test for the expected behavior, including the safe failure path." },
      { id: "interface", title: "Check the interface states", description: "Verify labels, keyboard focus, loading, empty, error, and right-to-left or long-copy states." },
      { id: "review", title: "Leave a rollback clue", description: "Record what you verified and how to undo or safely revisit the change if the assumption is wrong." },
    ],
  },
  ar: {
    eyebrow: "جاهزية البناء",
    title: "حوّل الفكرة الجيدة إلى تغيير آمن",
    description: "استخدم هذه البوابة القصيرة قبل فتح طلب دمج. فهي تُبقي خطوة التطوير التالية مركّزة ومختبرة وسهلة المراجعة.",
    workspaceNote: "تُحفظ قائمتك وملاحظتك داخل مساحة العمل هذه وعلى هذا الجهاز فقط.",
    checklistLabel: "قائمة جاهزية البناء",
    stepsLabel: "خطوات الجاهزية",
    progress: "اكتمل {done} من {total} فحوصات",
    complete: "مكتمل",
    markIncomplete: "تحديد كغير مكتمل",
    notesLabel: "ملاحظة البناء",
    notesPlaceholder: "سجّل النطاق أو أمر الاختبار أو المتابعة التي تريد تذكّرها…",
    notesHint: "الملاحظة محدودة بـ ٢٠٠٠ حرف ولا تغادر هذا المتصفح. احذف الأسرار والرموز وبيانات الاعتماد والبيانات الشخصية قبل الكتابة.",
    saved: "تم حفظ القائمة",
    storageError: "تعذّر حفظ القائمة على هذا الجهاز. يمكنك المتابعة، لكن راجعها قبل تبديل المتصفح.",
    loading: "جارٍ تحميل قائمة البناء…",
    noWorkspace: "تتوفر هذه القائمة بعد تجهيز مساحة العمل.",
    home: "العودة إلى الرئيسية",
    guide: "العودة إلى دليل التعلّم",
    steps: [
      { id: "scope", title: "سمِّ نتيجة واحدة", description: "حدّد النتيجة التي يراها المستخدم واجعل التغيير الأول صغيراً بما يكفي لمراجعته مرة واحدة." },
      { id: "boundary", title: "تحقق من الحدود", description: "حدّد حدود مساحة العمل والحساب والصلاحيات التي يجب أن يحترمها التغيير قبل التعديل." },
      { id: "contract", title: "اكتب اختبار العقد", description: "أضف اختباراً مركزاً للسلوك المتوقع، بما في ذلك مسار الفشل الآمن." },
      { id: "interface", title: "افحص حالات الواجهة", description: "تحقق من التسميات والتركيز بلوحة المفاتيح وحالات التحميل والفراغ والخطأ والنص الطويل ومن اليمين إلى اليسار." },
      { id: "review", title: "اترك دليلاً للتراجع", description: "سجّل ما تحققت منه وكيفية التراجع عن التغيير أو مراجعته بأمان إذا كان الافتراض غير صحيح." },
    ],
  },
  ku: {
    eyebrow: "ئامادەیی دروستکردن",
    title: "بیرۆکەیەکی باش بکە بە گۆڕانکارییەکی پارێزراو",
    description: "پێش کردنەوەی داواکارییەکی پێداچوونەوە ئەم دەروازە کورتە بەکاربهێنە. هەنگاوی داهاتووی گەشەپێدان چڕ و تاقیکراو و ئاسان بۆ گەڕانەوە دەهێڵێتەوە.",
    workspaceNote: "لیستەکەت و تێبینیت تەنها لەم شوێنکارە و لەم ئامێرەدا پاشەکەوت دەکرێن.",
    checklistLabel: "لیستی ئامادەیی دروستکردن",
    stepsLabel: "هەنگاوەکانی ئامادەیی",
    progress: "{done} لە {total} پشکنین تەواو کراوە",
    complete: "تەواوە",
    markIncomplete: "وەک تەواونەکراو نیشانە بکە",
    notesLabel: "تێبینیی دروستکردن",
    notesPlaceholder: "سنوورەکە، فەرمانی تاقیکردنەوە، یان بەدواداچوونێک بنووسە کە دەتەوێت لەبیرت بێت…",
    notesHint: "ئەم تێبینییە سنووری ٢٠٠٠ پیتە و لەم وێبگەڕە دەرناچێت. پێش نووسین نهێنی و تۆکن و زانیاریی دەسەڵات و داتای کەسی بسڕەوە.",
    saved: "لیستەکە پاشەکەوت کرا",
    storageError: "ئەم ئامێرە نەیتوانی لیستەکە پاشەکەوت بکات. دەتوانیت بەردەوام بیت، بەڵام پێش گۆڕینی وێبگەڕ بیپشکنەوە.",
    loading: "لیستی دروستکردنەکەت بار دەکرێت…",
    noWorkspace: "ئەم لیستە کاتێک بەردەست دەبێت کە شوێنکار ئامادە بێت.",
    home: "گەڕانەوە بۆ سەرەکی",
    guide: "گەڕانەوە بۆ ڕێبەری فێربوون",
    steps: [
      { id: "scope", title: "ئەنجامێکی تاک دیاری بکە", description: "ئەو ئەنجامە دیاری بکە کە بەکارهێنەر دەیبینێت و یەکەم گۆڕانکاری بەوەندە بچووک بهێڵەوە کە بە یەک جار پێداچوونەوەی بۆ بکرێت." },
      { id: "boundary", title: "سنوورەکە پشتڕاست بکەرەوە", description: "پێش دەستکاریکردن سنووری شوێنکار و هەژمار و مۆڵەتەکان دیاری بکە کە گۆڕانکارییەکە دەبێت ڕێزیان بگرێت." },
      { id: "contract", title: "تاقیکردنەوەی گرێبەست بنووسە", description: "تاقیکردنەوەیەکی دیاریکراو بۆ ڕەفتاری چاوەڕوانکراو زیاد بکە، لەگەڵ ڕێڕەوی هەڵەی پارێزراو." },
      { id: "interface", title: "دۆخەکانی ڕووکار بپشکنە", description: "ناونیشان و فۆکەسی کیبۆرد و دۆخی بارکردن و بەتاڵی و هەڵە و دەقی درێژ و ئاراستەی ڕاست بۆ چەپ پشتڕاست بکەرەوە." },
      { id: "review", title: "نیشانەیەک بۆ گەڕانەوە جێبهێڵە", description: "ئەوەی پشتڕاستت کردووەتەوە و چۆنیەتی هەڵوەشاندنەوە یان پێداچوونەوەی پارێزراو تۆمار بکە ئەگەر گریمانەکە هەڵە بوو." },
    ],
  },
};

function interpolate(template: string, values: Record<string, string>) {
  return Object.entries(values).reduce((result, [key, value]) => result.replace(`{${key}}`, value), template);
}

export function DevelopmentBuildChecklist() {
  const { locale } = useLocale();
  const { workspace } = useAuth();
  const strings = copy[locale];
  const [checklistState, setChecklistState] = useState<DevelopmentBuildState>(createDefaultDevelopmentBuildState);
  const [loadedWorkspaceId, setLoadedWorkspaceId] = useState<string | null>(null);
  const [persistenceError, setPersistenceError] = useState(false);
  const completedCount = useMemo(
    () => strings.steps.filter((step) => isDevelopmentBuildStepComplete(checklistState, step.id)).length,
    [checklistState, strings.steps],
  );

  useEffect(() => {
    if (!workspace?.id) return;
    // Workspace changes must load a different namespace before rendering old progress.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoadedWorkspaceId(null);
    setChecklistState(readDevelopmentBuildState(workspace.id));
    setLoadedWorkspaceId(workspace.id);
    setPersistenceError(false);
  }, [workspace?.id]);

  function persist(next: DevelopmentBuildState) {
    setChecklistState(next);
    if (!workspace?.id || !saveDevelopmentBuildState(workspace.id, next)) setPersistenceError(true);
    else setPersistenceError(false);
  }

  function toggleStep(stepId: string, completed: boolean) {
    persist(setDevelopmentBuildStepCompletion(checklistState, stepId, completed));
  }

  if (!workspace) {
    return <main className={styles.page}><section className={styles.empty} role="alert"><ShieldCheck size={28} aria-hidden="true" /><h1>{strings.noWorkspace}</h1><Link href="/" className={styles.secondaryAction}><ArrowLeft size={15} aria-hidden="true" /> {strings.home}</Link></section></main>;
  }
  if (loadedWorkspaceId !== workspace.id) {
    return <main className={styles.page}><div className={styles.loading} role="status" aria-live="polite"><RefreshCw size={18} aria-hidden="true" /> {strings.loading}</div></main>;
  }

  const progressLabel = interpolate(strings.progress, { done: String(completedCount), total: String(strings.steps.length) });
  return (
    <main className={styles.page} aria-labelledby="development-build-title">
      <header className={styles.header}>
        <div className={styles.headerLinks}>
          <Link href="/development/learn" className={styles.backLink}><ArrowLeft size={15} aria-hidden="true" /> {strings.guide}</Link>
          <Link href="/" className={styles.homeLink}>{strings.home}</Link>
        </div>
        <div className={styles.eyebrow}><ClipboardCheck size={14} aria-hidden="true" /> {strings.eyebrow}</div>
        <h1 id="development-build-title">{strings.title}</h1>
        <p className={styles.description}>{strings.description}</p>
        <p className={styles.workspaceNote}><LockKeyhole size={14} aria-hidden="true" /> {strings.workspaceNote}</p>
      </header>
      <section className={styles.shell} aria-label={strings.checklistLabel}>
        <div className={styles.shellHeader}>
          <div><p className={styles.sectionLabel}>{strings.stepsLabel}</p><h2>{strings.checklistLabel}</h2></div>
          <div className={styles.progressSummary}><strong>{progressLabel}</strong><div className={styles.progressTrack} role="progressbar" aria-valuemin={0} aria-valuemax={strings.steps.length} aria-valuenow={completedCount} aria-label={progressLabel}><span style={{ width: `${(completedCount / strings.steps.length) * 100}%` }} /></div></div>
        </div>
        <ol className={styles.stepList} aria-label={strings.stepsLabel}>
          {strings.steps.map((step, index) => {
            const completed = isDevelopmentBuildStepComplete(checklistState, step.id);
            return <li key={step.id} className={`${styles.step} ${completed ? styles.stepComplete : ""}`}><span className={styles.stepNumber} aria-hidden="true">{String(index + 1).padStart(2, "0")}</span><label className={styles.stepLabel}><input type="checkbox" checked={completed} onChange={(event) => toggleStep(step.id, event.target.checked)} aria-label={`${completed ? strings.markIncomplete : strings.complete}: ${step.title}`} /><span className={styles.checkbox} aria-hidden="true">{completed ? <Check size={14} /> : null}</span><span className={styles.stepCopy}><strong>{step.title}</strong><small>{step.description}</small></span></label><span className={styles.stepStatus}>{completed ? <><CheckCircle2 size={14} aria-hidden="true" /> {strings.complete}</> : null}</span></li>;
          })}
        </ol>
        <label className={styles.notes}><span>{strings.notesLabel}</span><textarea dir="auto" value={checklistState.note} onChange={(event) => persist(setDevelopmentBuildNote(checklistState, event.target.value))} maxLength={2_000} placeholder={strings.notesPlaceholder} aria-describedby="development-build-note-hint" /><small id="development-build-note-hint">{strings.notesHint} {checklistState.note.length}/2,000</small></label>
        <div className={styles.feedback} role="status" aria-live="polite">{persistenceError ? <><ShieldCheck size={14} aria-hidden="true" /> {strings.storageError}</> : <><Code2 size={14} aria-hidden="true" /> {strings.saved}</>}</div>
      </section>
    </main>
  );
}
