"use client";

import { useEffect, useState } from "react";
import { api, type MovieClipSegmentationPreview } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";

// A read-only duration preview. This is not a shot list, price quotation,
// provider execution approval, footage generation, or film export.
const copy = {
  en: {
    title: "Long-film duration preview",
    explanation: "View how the full runtime could fit within the connected adapter's declared clip-duration range.",
    loading: "Checking declared clip-duration capabilities…",
    unavailable: "A duration split is not available with the currently configured scene-clip adapter.",
    error: "The duration preview could not be loaded.",
    slots: "Duration slots",
    runtime: "Planned runtime",
    first: "First slot",
    last: "Last slot",
    range: "Declared clip range",
    seconds: "seconds",
    disclaimer: "Duration-only calculation. This does not plan shots, verify continuity, price or authorize generation, assemble clips, or create a finished film.",
  },
  ar: {
    title: "معاينة تقسيم مدة الفيلم الطويل",
    explanation: "اطّلع على كيفية تقسيم مدة الفيلم وفق حدود مدة المقطع التي يعلنها مزوّد الفيديو المتصل.",
    loading: "جارٍ التحقق من حدود مدة المقاطع…",
    unavailable: "لا تتوفر خطة لتقسيم المدة بإمكانات مزوّد المقاطع الحالي.",
    error: "تعذّر تحميل معاينة تقسيم المدة.",
    slots: "أجزاء المدة",
    runtime: "المدة المخططة",
    first: "الجزء الأول",
    last: "الجزء الأخير",
    range: "نطاق مدة المقطع المعلن",
    seconds: "ثانية",
    disclaimer: "حساب للمدة فقط؛ لا يخطط اللقطات، ولا يتحقق من استمرارية المشاهد أو الأسعار، ولا يجيز التوليد أو يجمع المقاطع أو ينتج فيلماً نهائياً.",
  },
  ku: {
    title: "پێشبینینی دابەشکردنی ماوەی فیلمی درێژ",
    explanation: "بزانە ماوەی گشتیی فیلم چۆن دەتوانرێت بەپێی سنووری ماوەی کلیپی دابینکەری پەیوەست دابەش بکرێت.",
    loading: "سنوورە ڕاگەیەندراوەکانی ماوەی کلیپ دەپشکنرێن…",
    unavailable: "بەپێی توانای دابینکەری ئێستا پلانی دابەشکردنی ماوە بەردەست نییە.",
    error: "نەتوانرا پێشبینینی دابەشکردنی ماوە باربکرێت.",
    slots: "بەشەکانی ماوە",
    runtime: "ماوەی پلانکراو",
    first: "بەشی یەکەم",
    last: "بەشی کۆتایی",
    range: "سنووری ڕاگەیەندراوی ماوەی کلیپ",
    seconds: "چرکە",
    disclaimer: "ئەمە تەنها ژماردنی ماوەیە؛ پلان بۆ کادرەکان دانانێت، بەردەوامی یان نرخ ناپشکنێت، مۆڵەتی بەرهەمهێنان نادات و کلیپەکان ناکۆکاتەوە.",
  },
} as const;

export function MovieClipSegmentationPanel({
  projectId,
  totalDurationSeconds,
}: {
  projectId: string;
  totalDurationSeconds: number;
}) {
  const { locale } = useLocale();
  const labels = copy[locale] ?? copy.en;
  const [preview, setPreview] = useState<MovieClipSegmentationPreview | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    let active = true;
    void api.getMovieClipSegmentationPreview(projectId).then((result) => {
      if (!active) return;
      // Never display a cached plan for a different project or runtime.
      if (result.movieProjectId !== projectId || result.totalDurationSeconds !== totalDurationSeconds) {
        setError(true);
        return;
      }
      setPreview(result);
    }).catch(() => {
      if (active) setError(true);
    });
    return () => { active = false; };
  }, [projectId, totalDurationSeconds]);

  const valid = preview?.movieProjectId === projectId
    && preview.totalDurationSeconds === totalDurationSeconds;
  const number = (value: number) => new Intl.NumberFormat(locale === "ku" ? "ckb" : locale).format(value);

  return (
    <section className="movie-command-section" aria-label={labels.title}>
      <div className="movie-command-section-title">
        <div><span className="movie-workspace-kicker">{labels.runtime}: {number(totalDurationSeconds)} {labels.seconds}</span><h3>{labels.title}</h3></div>
        <small>{labels.explanation}</small>
      </div>
      {error ? <p className="movie-command-note" role="status">{labels.error}</p>
        : (!valid || !preview) ? <p className="movie-command-note" role="status">{labels.loading}</p>
          : !preview.canSplitByDuration || !preview.segments.length
            ? <p className="movie-command-note" role="status">{labels.unavailable}</p>
            : <div className="movie-health-list">
                <div className="movie-health-line"><span>{labels.slots}</span><strong>{number(preview.segments.length)}</strong></div>
                <div className="movie-health-line"><span>{labels.range}</span><strong>{number(preview.minimumClipSeconds ?? 0)}–{number(preview.maximumClipSeconds ?? 0)} {labels.seconds}</strong></div>
                <div className="movie-health-line"><span>{labels.first}</span><strong>{number(preview.segments[0].durationSeconds)} {labels.seconds}</strong></div>
                <div className="movie-health-line"><span>{labels.last}</span><strong>{number(preview.segments[preview.segments.length - 1].durationSeconds)} {labels.seconds}</strong></div>
              </div>}
      <p className="movie-command-note">{labels.disclaimer}</p>
    </section>
  );
}
