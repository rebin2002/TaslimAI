"use client";

import { useState, type FormEvent } from "react";
import { api } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";

// A Full Movie's draft runtime is editable only until planning starts.
// The backend independently enforces Edit permission, Draft status, no
// scenes/clips, the 7,200-second cap, and CSRF protection.
const copy = {
  en: {
    title: "Draft film runtime",
    details: "Adjust the planned film length before creating scenes. Maximum: 120 minutes.",
    label: "Duration (seconds)",
    save: "Save duration",
    saving: "Saving…",
    saved: "Draft duration saved.",
    invalid: "Enter a whole number between 1 and 7,200 seconds.",
    failed: "Could not save the draft runtime.",
    locked: "The runtime is locked after scene planning or generation begins. Editing an established timeline requires a separately reviewed replan.",
  },
  ar: {
    title: "مدة مسودة الفيلم",
    details: "اضبط مدة الفيلم قبل إنشاء المشاهد. الحد الأقصى: 120 دقيقة.",
    label: "المدة (بالثواني)",
    save: "حفظ المدة",
    saving: "جارٍ الحفظ…",
    saved: "تم حفظ مدة المسودة.",
    invalid: "أدخل عدداً صحيحاً بين 1 و7,200 ثانية.",
    failed: "تعذّر حفظ مدة المسودة.",
    locked: "تُقفل المدة بعد بدء تخطيط المشاهد أو التوليد. يتطلب تغيير جدول زمني قائم إعادة تخطيط ومراجعة مستقلة.",
  },
  ku: {
    title: "ماوەی ڕەشنووسی فیلم",
    details: "پێش دروستکردنی دیمەنەکان ماوەی فیلم ڕێک بخە. زۆرترین: ١٢٠ خولەک.",
    label: "ماوە (بە چرکە)",
    save: "ماوە پاشەکەوت بکە",
    saving: "پاشەکەوت دەکرێت…",
    saved: "ماوەی ڕەشنووس پاشەکەوت کرا.",
    invalid: "ژمارەیەکی تەواو لە نێوان ١ و ٧٢٠٠ چرکە بنووسە.",
    failed: "نەتوانرا ماوەی ڕەشنووس پاشەکەوت بکرێت.",
    locked: "دوای دەستپێکردنی پلانی دیمەنەکان یان بەرهەمهێنان، ماوەکە قوفڵ دەبێت. گۆڕینی هێڵی کاتی ئامادە پێویستی بە پلانکردنەوە و پشکنینی سەربەخۆ هەیە.",
  },
} as const;

export function MovieProjectDurationEditor({
  projectId,
  durationSeconds,
  canEditDraft,
  onUpdated,
}: {
  projectId: string;
  durationSeconds: number;
  canEditDraft: boolean;
  onUpdated: () => Promise<void>;
}) {
  const { locale } = useLocale();
  const labels = copy[locale] ?? copy.en;
  const [value, setValue] = useState(String(durationSeconds));
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState("");
  const [error, setError] = useState("");

  const number = Number(value);
  const valid = /^\\d+$/.test(value) && Number.isSafeInteger(number)
    && number >= 1 && number <= 7_200;
  const unchanged = valid && number === durationSeconds;

  async function handleSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy || !canEditDraft || unchanged) return;
    if (!valid) {
      setError(labels.invalid);
      return;
    }
    setBusy(true);
    setNotice("");
    setError("");
    try {
      const updated = await api.updateMovieProjectDuration(projectId, number);
      if (updated.id !== projectId || updated.durationSeconds !== number)
        throw new Error(labels.failed);
      setNotice(labels.saved);
      await onUpdated();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : labels.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="movie-command-section" aria-label={labels.title}>
      <div className="movie-command-section-title">
        <div><span className="movie-workspace-kicker">{labels.label}</span><h3>{labels.title}</h3></div>
        <small>{labels.details}</small>
      </div>
      {canEditDraft ? (
        <form onSubmit={(event) => void handleSave(event)}>
          <label htmlFor="movie-draft-duration-seconds">{labels.label}</label>
          <input
            id="movie-draft-duration-seconds"
            name="durationSeconds"
            type="number"
            min={1}
            max={7200}
            step={1}
            required
            value={value}
            disabled={busy}
            onChange={(event) => { setValue(event.target.value); setError(""); setNotice(""); }}
          />
          <button type="submit" className="movie-workspace-button is-primary" disabled={busy || !valid || unchanged}>
            {busy ? labels.saving : labels.save}
          </button>
          {notice && <p className="movie-command-note" role="status">{notice}</p>}
          {error && <p className="movie-command-note" role="alert">{error}</p>}
        </form>
      ) : <p className="movie-command-note">{labels.locked}</p>}
    </section>
  );
}
