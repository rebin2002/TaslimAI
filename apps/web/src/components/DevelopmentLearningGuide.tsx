"use client";

import Link from "next/link";
import { ArrowLeft, Check, CheckCircle2, Code2, FileCode2, GitBranch, LockKeyhole, RefreshCw, ShieldCheck, TerminalSquare } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import type { Locale } from "@/lib/i18n";
import {
  createDefaultDevelopmentLearningState,
  isDevelopmentLessonComplete,
  readDevelopmentLearningState,
  saveDevelopmentLearningState,
  setDevelopmentLearningNotes,
  setDevelopmentLessonCompletion,
  type DevelopmentLearningState,
  type LearningTrackId,
} from "@/lib/developmentLearningState";
import styles from "./DevelopmentLearningGuide.module.css";

type Lesson = { id: string; title: string; description: string };
type Track = { title: string; summary: string; lessons: Lesson[] };
type Copy = {
  eyebrow: string;
  title: string;
  description: string;
  workspaceNote: string;
  trackLabel: string;
  lessonsLabel: string;
  notesLabel: string;
  notesPlaceholder: string;
  notesHint: string;
  progress: string;
  complete: string;
  markIncomplete: string;
  saved: string;
  storageError: string;
  loading: string;
  noWorkspace: string;
  retry: string;
  home: string;
  tracks: Record<LearningTrackId, Track>;
};

const trackIcons: Record<LearningTrackId, typeof Code2> = {
  foundations: GitBranch,
  frontend: FileCode2,
  backend: TerminalSquare,
};

const copy: Record<Locale, Copy> = {
  en: {
    eyebrow: "Development learning guide",
    title: "Build your next reliable feature",
    description: "A small, practical path for learning the habits that make software easier to ship, review, and maintain.",
    workspaceNote: "Your progress and notes are saved only in this workspace on this device.",
    trackLabel: "Learning tracks",
    lessonsLabel: "Lessons",
    notesLabel: "Workspace notes",
    notesPlaceholder: "Capture a question, command, or next step…",
    notesHint: "Notes are limited to 2,000 characters and never leave this browser.",
    progress: "{done} of {total} lessons complete",
    complete: "Mark complete",
    markIncomplete: "Mark incomplete",
    saved: "Progress saved",
    storageError: "Progress could not be saved on this device. You can continue, but revisit this guide before switching browsers.",
    loading: "Loading your workspace guide…",
    noWorkspace: "This guide is unavailable until a workspace is ready.",
    retry: "Try again",
    home: "Back to home",
    tracks: {
      foundations: { title: "Engineering foundations", summary: "Learn the small habits that keep changes understandable and reversible.", lessons: [
        { id: "foundations-git", title: "Make one change easy to review", description: "Keep a focused branch, a clear commit, and a short explanation of the behavior you verified." },
        { id: "foundations-debug", title: "Debug from evidence", description: "Reproduce the failure, narrow the boundary, and record the smallest useful signal before editing." },
        { id: "foundations-tests", title: "Test the contract", description: "Turn the expected behavior into a focused test before relying on a manual happy path." },
      ] },
      frontend: { title: "Accessible frontend", summary: "Build interfaces that remain understandable with a keyboard, screen reader, or translated copy.", lessons: [
        { id: "frontend-a11y", title: "Give every control a purpose", description: "Use a visible label or accessible name, a clear focus state, and a state announcement for async work." },
        { id: "frontend-state", title: "Keep state scoped", description: "Tie cached or durable client state to the workspace and discard malformed or stale values safely." },
        { id: "frontend-copy", title: "Design for translation", description: "Keep copy concise, avoid layout assumptions, and verify both right-to-left direction and long labels." },
      ] },
      backend: { title: "Safe backend", summary: "Protect boundaries while keeping failures actionable for the person using the product.", lessons: [
        { id: "backend-auth", title: "Authorize from server truth", description: "Resolve ownership and membership on the server; never treat a browser-supplied workspace id as proof." },
        { id: "backend-errors", title: "Return safe failures", description: "Expose a stable, actionable error class without leaking credentials, provider internals, or sensitive records." },
        { id: "backend-state", title: "Make writes durable", description: "Validate inputs, make retries safe where possible, and test the persisted result rather than only the response." },
      ] },
    },
  },
  ar: {
    eyebrow: "دليل تعلّم التطوير",
    title: "ابنِ ميزتك الموثوقة التالية",
    description: "مسار عملي صغير لتعلّم العادات التي تجعل شحن البرامج ومراجعتها وصيانتها أسهل.",
    workspaceNote: "يُحفظ تقدّمك وملاحظاتك داخل مساحة العمل هذه وعلى هذا الجهاز فقط.",
    trackLabel: "مسارات التعلّم",
    lessonsLabel: "الدروس",
    notesLabel: "ملاحظات مساحة العمل",
    notesPlaceholder: "سجّل سؤالاً أو أمراً أو خطوة تالية…",
    notesHint: "الملاحظات محدودة بـ ٢٠٠٠ حرف ولا تغادر هذا المتصفح.",
    progress: "اكتمل {done} من {total} دروس",
    complete: "تحديد كمكتمل",
    markIncomplete: "إلغاء الاكتمال",
    saved: "تم حفظ التقدّم",
    storageError: "تعذّر حفظ التقدّم على هذا الجهاز. يمكنك المتابعة، لكن راجع هذا الدليل قبل تبديل المتصفح.",
    loading: "جارٍ تحميل دليل مساحة العمل…",
    noWorkspace: "يتوفر هذا الدليل بعد تجهيز مساحة العمل.",
    retry: "حاول مجدداً",
    home: "العودة إلى الرئيسية",
    tracks: {
      foundations: { title: "أساسيات الهندسة", summary: "تعلّم العادات الصغيرة التي تُبقي التغييرات مفهومة وقابلة للعكس.", lessons: [
        { id: "foundations-git", title: "اجعل التغيير سهل المراجعة", description: "حافظ على فرع مركز ونسخة واضحة وشرح قصير للسلوك الذي تحققت منه." },
        { id: "foundations-debug", title: "صحّح الأخطاء بالدليل", description: "أعد إنتاج المشكلة، وحدد حدودها، وسجّل أصغر إشارة مفيدة قبل التعديل." },
        { id: "foundations-tests", title: "اختبر العقد", description: "حوّل السلوك المتوقع إلى اختبار مركز قبل الاعتماد على المسار السعيد اليدوي." },
      ] },
      frontend: { title: "واجهة أمامية ميسّرة", summary: "ابنِ واجهات مفهومة بلوحة المفاتيح وقارئ الشاشة والنص المترجم.", lessons: [
        { id: "frontend-a11y", title: "امنح كل عنصر تحكم غرضاً", description: "استخدم اسماً واضحاً وحالة تركيز وإعلاناً لحالة العمل غير المتزامن." },
        { id: "frontend-state", title: "اجعل الحالة معزولة", description: "اربط الحالة المخزنة بمساحة العمل وتخلّص بأمان من القيم التالفة أو القديمة." },
        { id: "frontend-copy", title: "صمّم للترجمة", description: "اجعل النص موجزاً وتحقق من الاتجاه من اليمين إلى اليسار ومن العناوين الطويلة." },
      ] },
      backend: { title: "خلفية آمنة", summary: "احمِ الحدود مع إبقاء الأعطال مفيدة وقابلة للتصرف للمستخدم.", lessons: [
        { id: "backend-auth", title: "فوّض من حقيقة الخادم", description: "تحقق من الملكية والعضوية على الخادم، ولا تعتبر معرّف مساحة العمل من المتصفح دليلاً." },
        { id: "backend-errors", title: "أعد أعطالاً آمنة", description: "اعرض خطأ مستقراً وقابلاً للتصرف دون كشف الأسرار أو تفاصيل المزوّد أو السجلات الحساسة." },
        { id: "backend-state", title: "اجعل الكتابات دائمة", description: "تحقق من المدخلات واجعل الإعادة آمنة واختبر النتيجة المحفوظة لا الاستجابة فقط." },
      ] },
    },
  },
  ku: {
    eyebrow: "ڕێبەری فێربوونی گەشەپێدان",
    title: "تایبەتمەندییەکی بەهێزی داهاتوو دروست بکە",
    description: "ڕێگایەکی کرداریی کورت بۆ فێربوونی ئەو ڕەفتارانەی کە ناردن و پێداچوونەوە و چاککردنەوەی نەرمامەڵە ئاسانتر دەکەن.",
    workspaceNote: "پێشکەوتن و تێبینییەکانت تەنها لەم شوێنکارە و لەم ئامێرەدا پاشەکەوت دەکرێن.",
    trackLabel: "ڕێڕەوەکانی فێربوون",
    lessonsLabel: "وانەکان",
    notesLabel: "تێبینییەکانی شوێنکار",
    notesPlaceholder: "پرسیارێک، فەرمانێک، یان هەنگاوی داهاتوو بنووسە…",
    notesHint: "تێبینییەکان سنووری ٢٠٠٠ پیتن و لەم وێبگەڕە دەرناچن.",
    progress: "{done} لە {total} وانە تەواو کراوە",
    complete: "وەک تەواوکراو نیشانە بکە",
    markIncomplete: "نیشانەی تەواوبوون لاببە",
    saved: "پێشکەوتن پاشەکەوت کرا",
    storageError: "پێشکەوتن لەم ئامێرەدا پاشەکەوت نەکرا. دەتوانیت بەردەوام بیت، بەڵام پێش گۆڕینی وێبگەڕ ئەم ڕێبەرە بپشکنەوە.",
    loading: "ڕێبەری شوێنکارەکەت بار دەکرێت…",
    noWorkspace: "ئەم ڕێبەرە کاتێک بەردەست دەبێت کە شوێنکار ئامادە بێت.",
    retry: "دووبارە هەوڵ بدەرەوە",
    home: "گەڕانەوە بۆ سەرەکی",
    tracks: {
      foundations: { title: "بنەماکانی ئەندازیاری", summary: "ڕەفتارە بچووکەکان فێربە کە گۆڕانکارییەکان تێگەیشتوو و گەڕاوە دەهێڵن.", lessons: [
        { id: "foundations-git", title: "گۆڕانکارییەکە ئاسان بۆ پێداچوونەوە بکە", description: "لقێکی دیاریکراو و کۆمیتێکی ڕوون و ڕوونکردنەوەیەکی کورت بۆ ئەو ڕەفتارەی پشتڕاستت کردووەتەوە بەکاربهێنە." },
        { id: "foundations-debug", title: "لەسەر بنەمای بەڵگە هەڵە چاک بکە", description: "کێشەکە دووبارە دروست بکە، سنوورەکەی دیاری بکە و پێش دەستکاریکردن بچووکترین نیشانەی بەسوود تۆمار بکە." },
        { id: "foundations-tests", title: "گرێبەستەکە تاقی بکەرەوە", description: "ڕەفتاری چاوەڕوانکراو بکە بە تاقیکردنەوەیەکی دیاریکراو پێش پشتبەستن بە ڕێڕەوی دەستی." },
      ] },
      frontend: { title: "پێشەوەی دەستگەیشتوو", summary: "ڕووکار دروست بکە کە بە کیبۆرد و خوێنەری شاشە و دەقی وەرگێڕدراو تێگەیشتوو بمێنێتەوە.", lessons: [
        { id: "frontend-a11y", title: "بۆ هەر کۆنترۆڵێک مەبەستێک دابین بکە", description: "ناونیشانێکی ڕوون، دۆخی فۆکەس و ئاگادارکردنەوەی دۆخی کارە ناهاوکاتەکە بەکاربهێنە." },
        { id: "frontend-state", title: "دۆخەکە سنووردار بهێڵە", description: "دۆخی هەڵگیراو بە شوێنکارەکەوە ببەستە و بە پارێزراوی نرخی خراپ یان کۆن فڕێ بدە." },
        { id: "frontend-copy", title: "بۆ وەرگێڕان دیزاین بکە", description: "دەقەکە کورت بهێڵە و ئاراستەی ڕاست بۆ چەپ و ناونیشانە درێژەکان پشتڕاست بکەرەوە." },
      ] },
      backend: { title: "پاشەوەی پارێزراو", summary: "سنوورەکان بپارێزە و لە هەمان کاتدا هەڵەکان بۆ بەکارهێنەر بەسوود و کرداری بن.", lessons: [
        { id: "backend-auth", title: "لە ڕاستی ڕاژەکارەوە دەسەڵات بدە", description: "خاوەندارێتی و ئەندامێتی لە ڕاژەکار پشتڕاست بکەوە؛ ناسێنەری شوێنکاری براوزەر بە بەڵگە مەزانە." },
        { id: "backend-errors", title: "هەڵەی پارێزراو بگەڕێنەوە", description: "هەڵەیەکی جێگیر و کرداری پیشان بدە بەبێ ئاشکراکردنی نهێنی و وردەکاریی دابینکەر یان تۆمارە هەستیارەکان." },
        { id: "backend-state", title: "نووسینەکان دایمی بکە", description: "دەروازەکان پشتڕاست بکەوە، دووبارەکردنەوە تا ڕادەیەک پارێزراو بکە و ئەنجامی پاشەکەوتکراو تاقی بکەرەوە." },
      ] },
    },
  },
};

function interpolate(template: string, values: Record<string, string>) {
  return Object.entries(values).reduce((result, [key, value]) => result.replace(`{${key}}`, value), template);
}

export function DevelopmentLearningGuide() {
  const { locale } = useLocale();
  const { workspace } = useAuth();
  const strings = copy[locale];
  const [guideState, setGuideState] = useState<DevelopmentLearningState>(createDefaultDevelopmentLearningState);
  const [loadedWorkspaceId, setLoadedWorkspaceId] = useState<string | null>(null);
  const [persistenceError, setPersistenceError] = useState(false);

  useEffect(() => {
    if (!workspace?.id) return;
    // Workspace changes must load a different namespace before rendering old progress.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoadedWorkspaceId(null);
    setGuideState(readDevelopmentLearningState(workspace.id));
    setLoadedWorkspaceId(workspace.id);
    setPersistenceError(false);
  }, [workspace?.id]);

  const activeTrack = strings.tracks[guideState.activeTrack];
  const progress = useMemo(() => {
    const done = activeTrack.lessons.filter((lesson) => isDevelopmentLessonComplete(guideState, lesson.id)).length;
    return { done, total: activeTrack.lessons.length };
  }, [activeTrack.lessons, guideState]);

  function persist(next: DevelopmentLearningState) {
    setGuideState(next);
    if (!workspace?.id || !saveDevelopmentLearningState(workspace.id, next)) setPersistenceError(true);
    else setPersistenceError(false);
  }

  function chooseTrack(trackId: LearningTrackId) {
    persist({ ...guideState, activeTrack: trackId, updatedAt: new Date().toISOString() });
  }

  function toggleLesson(lessonId: string, completed: boolean) {
    persist(setDevelopmentLessonCompletion(guideState, lessonId, completed));
  }

  if (!workspace) {
    return <main className={styles.page}><section className={styles.empty} role="alert"><ShieldCheck size={28} aria-hidden="true" /><h1>{strings.noWorkspace}</h1><Link href="/" className={styles.secondaryAction}><ArrowLeft size={15} aria-hidden="true" /> {strings.home}</Link></section></main>;
  }

  if (loadedWorkspaceId !== workspace.id) {
    return <main className={styles.page}><div className={styles.loading} role="status" aria-live="polite"><RefreshCw size={18} aria-hidden="true" /> {strings.loading}</div></main>;
  }

  return (
    <main className={styles.page} aria-labelledby="development-learning-title">
      <header className={styles.header}>
        <Link href="/" className={styles.backLink}><ArrowLeft size={15} aria-hidden="true" /> {strings.home}</Link>
        <div className={styles.eyebrow}><Code2 size={14} aria-hidden="true" /> {strings.eyebrow}</div>
        <h1 id="development-learning-title">{strings.title}</h1>
        <p className={styles.description}>{strings.description}</p>
        <p className={styles.workspaceNote}><LockKeyhole size={14} aria-hidden="true" /> {strings.workspaceNote}</p>
      </header>

      <section className={styles.shell} aria-label={strings.trackLabel}>
        <nav className={styles.trackList} role="tablist" aria-label={strings.trackLabel}>
          {(Object.keys(trackIcons) as LearningTrackId[]).map((trackId) => {
            const Icon = trackIcons[trackId];
            const track = strings.tracks[trackId];
            const selected = guideState.activeTrack === trackId;
            return <button key={trackId} id={`development-tab-${trackId}`} type="button" role="tab" aria-selected={selected} aria-controls={`development-track-${trackId}`} className={`${styles.trackButton} ${selected ? styles.selected : ""}`} onClick={() => chooseTrack(trackId)}><Icon size={18} aria-hidden="true" /><span><strong>{track.title}</strong><small>{track.summary}</small></span><span className={styles.trackArrow} aria-hidden="true">{selected ? "●" : "○"}</span></button>;
          })}
        </nav>

        <section id={`development-track-${guideState.activeTrack}`} className={styles.content} role="tabpanel" tabIndex={0} aria-labelledby={`development-tab-${guideState.activeTrack}`}>
          <div className={styles.contentHeader}>
            <div><p className={styles.sectionLabel}>{strings.lessonsLabel}</p><h2>{activeTrack.title}</h2><p>{activeTrack.summary}</p></div>
            <div className={styles.progressSummary}><strong>{interpolate(strings.progress, { done: String(progress.done), total: String(progress.total) })}</strong><div className={styles.progressTrack} role="progressbar" aria-valuemin={0} aria-valuemax={progress.total} aria-valuenow={progress.done} aria-label={interpolate(strings.progress, { done: String(progress.done), total: String(progress.total) })}><span style={{ width: `${progress.total ? (progress.done / progress.total) * 100 : 0}%` }} /></div></div>
          </div>
          <ol className={styles.lessonList} aria-label={strings.lessonsLabel}>
            {activeTrack.lessons.map((lesson) => {
              const completed = isDevelopmentLessonComplete(guideState, lesson.id);
              return <li key={lesson.id} className={`${styles.lesson} ${completed ? styles.lessonComplete : ""}`}><label className={styles.lessonLabel}><input type="checkbox" checked={completed} onChange={(event) => toggleLesson(lesson.id, event.target.checked)} aria-label={`${completed ? strings.markIncomplete : strings.complete}: ${lesson.title}`} /><span className={styles.checkbox} aria-hidden="true">{completed ? <Check size={14} /> : null}</span><span className={styles.lessonCopy}><strong>{lesson.title}</strong><small>{lesson.description}</small></span></label><span className={styles.lessonStatus}>{completed ? <><CheckCircle2 size={14} aria-hidden="true" /> {strings.complete}</> : null}</span></li>;
            })}
          </ol>
          <label className={styles.notes}><span>{strings.notesLabel}</span><textarea value={guideState.notes} onChange={(event) => persist(setDevelopmentLearningNotes(guideState, event.target.value))} maxLength={2_000} placeholder={strings.notesPlaceholder} aria-describedby="development-notes-hint" /><small id="development-notes-hint">{strings.notesHint} {guideState.notes.length}/2,000</small></label>
          <div className={styles.feedback} role="status" aria-live="polite">{persistenceError ? <><ShieldCheck size={14} aria-hidden="true" /> {strings.storageError}</> : <><CheckCircle2 size={14} aria-hidden="true" /> {strings.saved}</>}</div>
        </section>
      </section>
    </main>
  );
}
