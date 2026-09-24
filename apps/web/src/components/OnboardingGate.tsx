"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import {
  ArrowLeft,
  ArrowRight,
  Check,
  FileText,
  FolderKanban,
  Globe2,
  Image,
  Languages,
  MessageSquare,
  Presentation,
  Search,
  Sparkles,
  type LucideIcon,
} from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { localeNames, locales, useLocale } from "@/components/LocaleProvider";
import { onboardingWorkflowDefinitions, shouldShowOnboarding, type OnboardingIntent } from "@/lib/onboarding";
import type { Locale } from "@/lib/i18n";

const workflowIcons: Record<OnboardingIntent, LucideIcon> = {
  project: FolderKanban,
  chat: MessageSquare,
  image: Image,
  document: FileText,
  presentation: Presentation,
  research: Search,
};

type OnboardingStep = "preferences" | "tour" | "workflow";

export function OnboardingGate() {
  const { user, completeOnboarding } = useAuth();
  const { locale, setLocale, t } = useLocale();
  const router = useRouter();
  const [step, setStep] = useState<OnboardingStep>("preferences");
  const [displayName, setDisplayName] = useState(user?.displayName ?? "");
  const [preferredLanguage, setPreferredLanguage] = useState<Locale>(user?.preferredLanguage ?? locale);
  const [defaultGenerationLanguage, setDefaultGenerationLanguage] = useState<Locale>(user?.defaultGenerationLanguage ?? locale);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");

  if (!shouldShowOnboarding(user)) return null;

  const steps: OnboardingStep[] = ["preferences", "tour", "workflow"];
  const currentStep = steps.indexOf(step) + 1;
  const NextIcon = locale === "en" ? ArrowRight : ArrowLeft;

  async function finish(intent?: OnboardingIntent, href = "/projects") {
    setSaving(true);
    setError("");
    try {
      await completeOnboarding({
        displayName: displayName.trim() || undefined,
        preferredLanguage,
        defaultGenerationLanguage,
        intent,
      });
      setLocale(preferredLanguage);
      router.push(href);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("onboarding.saveError"));
      setSaving(false);
    }
  }

  return (
    <div className="modal-backdrop onboarding-backdrop" role="presentation">
      <section className="onboarding-card" role="dialog" aria-modal="true" aria-labelledby="onboarding-title">
        <header className="onboarding-header">
          <div className="onboarding-brand"><span className="onboarding-brand-mark"><Sparkles size={16} /></span><span>Taslim.ai</span></div>
          <button type="button" className="onboarding-skip" onClick={() => void finish()} disabled={saving}>{t("onboarding.skip")}</button>
        </header>

        <div className="onboarding-progress" aria-label={t("onboarding.progress", { current: String(currentStep), total: String(steps.length) })}>
          {steps.map((item, index) => <span key={item} className={index < currentStep ? "is-complete" : ""} />)}
        </div>

        {step === "preferences" && <div className="onboarding-body">
          <p className="section-eyebrow">{t("onboarding.eyebrow")}</p>
          <h1 id="onboarding-title">{t("onboarding.preferencesTitle")}</h1>
          <p className="onboarding-description">{t("onboarding.preferencesDescription")}</p>
          <div className="onboarding-fields">
            <label><span>{t("onboarding.displayName")}</span><input value={displayName} maxLength={120} onChange={(event) => setDisplayName(event.target.value)} placeholder={t("onboarding.displayNamePlaceholder")} autoComplete="name" /></label>
            <label><span><Globe2 size={15} /> {t("onboarding.interfaceLanguage")}</span><select value={preferredLanguage} onChange={(event) => { const next = event.target.value as Locale; setPreferredLanguage(next); setLocale(next); }}>{locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}</select></label>
            <label><span><Languages size={15} /> {t("onboarding.generationLanguage")}</span><select value={defaultGenerationLanguage} onChange={(event) => setDefaultGenerationLanguage(event.target.value as Locale)}>{locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}</select></label>
          </div>
          <footer className="onboarding-actions"><span>{t("onboarding.preferencesHint")}</span><button type="button" className="primary-button" onClick={() => setStep("tour")}><span>{t("onboarding.continue")}</span><NextIcon size={16} /></button></footer>
        </div>}

        {step === "tour" && <div className="onboarding-body">
          <p className="section-eyebrow">{t("onboarding.tourEyebrow")}</p>
          <h1 id="onboarding-title">{t("onboarding.tourTitle")}</h1>
          <p className="onboarding-description">{t("onboarding.tourDescription")}</p>
          <div className="onboarding-intro-grid">
            <article><span className="onboarding-intro-icon is-project"><FolderKanban size={19} /></span><div><h2>{t("onboarding.projectsTitle")}</h2><p>{t("onboarding.projectsDescription")}</p></div></article>
            <article><span className="onboarding-intro-icon is-chat"><MessageSquare size={19} /></span><div><h2>{t("onboarding.chatTitle")}</h2><p>{t("onboarding.chatDescription")}</p></div></article>
            <article><span className="onboarding-intro-icon is-create"><Sparkles size={19} /></span><div><h2>{t("onboarding.createTitle")}</h2><p>{t("onboarding.createDescription")}</p></div></article>
          </div>
          <footer className="onboarding-actions"><button type="button" className="onboarding-back" onClick={() => setStep("preferences")}>{t("onboarding.back")}</button><button type="button" className="primary-button" onClick={() => setStep("workflow")}><span>{t("onboarding.chooseAction")}</span><NextIcon size={16} /></button></footer>
        </div>}

        {step === "workflow" && <div className="onboarding-body onboarding-workflow-body">
          <p className="section-eyebrow">{t("onboarding.workflowEyebrow")}</p>
          <h1 id="onboarding-title">{t("create.title")}</h1>
          <p className="onboarding-description">{t("onboarding.workflowDescription")}</p>
          <div className="onboarding-workflow-grid">
            {onboardingWorkflowDefinitions.map((workflow) => {
              const Icon = workflowIcons[workflow.intent];
              return <button type="button" key={workflow.intent} className="onboarding-workflow" onClick={() => void finish(workflow.intent, workflow.href)} disabled={saving}>
                <span className="onboarding-workflow-icon"><Icon size={19} /></span>
                <span><strong>{t(workflow.titleKey)}</strong><small>{t(workflow.descriptionKey)}</small></span>
                <NextIcon className="onboarding-workflow-arrow" size={16} />
              </button>;
            })}
          </div>
          <footer className="onboarding-actions"><button type="button" className="onboarding-back" onClick={() => setStep("tour")} disabled={saving}>{t("onboarding.back")}</button><span>{t("onboarding.chooseHint")}</span></footer>
        </div>}

        {error && <p className="onboarding-error" role="alert">{error}</p>}
        {saving && <div className="onboarding-saving" aria-live="polite"><Check size={15} /> {t("onboarding.saving")}</div>}
      </section>
    </div>
  );
}
