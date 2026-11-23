"use client";

import { FormEvent, useEffect, useRef, useState } from "react";
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
  // Identity of the project that owns this mounted instance's draft, its save
  // feedback, and any request still in flight. The view stays mounted while the
  // route parameter changes, so a switch of project - and with it of workspace -
  // has to drop the previous project's state rather than display, submit or
  // confirm it as the new project's context.
  const ownerRef = useRef(project.id);

  useEffect(() => {
    if (ownerRef.current === project.id) return;
    ownerRef.current = project.id;
    setInstructions(project.instructions ?? "");
    setContextNotes(project.contextNotes ?? "");
    setSaving(false);
    setSaved(false);
    setError("");
    // Project identity intentionally controls the local draft boundary.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    const submittingProjectId = project.id;
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
      const updated = await api.updateProject(submittingProjectId, input);
      // A reply that lands after the view moved on belongs to the project that has
      // just been left behind, so it is dropped instead of being written into the
      // overview of the project now on screen.
      if (ownerRef.current !== submittingProjectId) return;
      onSaved(updated);
      setSaved(true);
    } catch (caught) {
      if (ownerRef.current !== submittingProjectId) return;
      setError(caught instanceof Error ? caught.message : t("projects.saveError"));
    } finally {
      // The switch above already cleared the saving flag for the incoming project.
      if (ownerRef.current === submittingProjectId) setSaving(false);
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
