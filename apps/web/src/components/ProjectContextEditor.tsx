"use client";

import { FormEvent, useState } from "react";
import { Check, FileText } from "lucide-react";
import { api, type Project, type ProjectInput } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";

export function ProjectContextEditor({ project, onSaved }: Readonly<{ project: Project; onSaved: (project: Project) => void }>) {
  const { t } = useLocale();
  const [instructions, setInstructions] = useState(project.instructions ?? "");
  const [contextNotes, setContextNotes] = useState(project.contextNotes ?? "");
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState("");

  async function submit(event: FormEvent) {
    event.preventDefault();
    setSaving(true);
    setSaved(false);
    setError("");
    const input: ProjectInput = {
      name: project.name,
      description: project.description ?? undefined,
      type: project.type,
      instructions: instructions || undefined,
      contextNotes: contextNotes || undefined,
    };
    try {
      const updated = await api.updateProject(project.id, input);
      onSaved(updated);
      setSaved(true);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("projects.saveError"));
    } finally {
      setSaving(false);
    }
  }

  return <form className="project-context-card account-card" onSubmit={submit}>
    <div className="card-title"><span className="card-title-icon teal"><FileText size={17} /></span><div><h2>{t("projects.contextTitle")}</h2><p>{t("projects.contextDescription")}</p></div></div>
    <label><span>{t("projects.instructions")}</span><textarea maxLength={4000} rows={4} value={instructions} onChange={(event) => setInstructions(event.target.value)} placeholder={t("projects.instructionsPlaceholder")} /></label>
    <label><span>{t("projects.contextNotes")}</span><textarea maxLength={8000} rows={5} value={contextNotes} onChange={(event) => setContextNotes(event.target.value)} placeholder={t("projects.contextNotesPlaceholder")} /></label>
    {error && <div className="form-error" role="alert">{error}</div>}
    {saved && <div className="form-success"><Check size={15} /> {t("projects.contextSaved")}</div>}
    <button className="primary-button" disabled={saving}>{saving ? t("common.saving") : <><Check size={15} /> {t("projects.saveContext")}</>}</button>
  </form>;
}
