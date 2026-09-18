"use client";

import { FormEvent, useState } from "react";
import { Check, ChevronDown, X } from "lucide-react";
import type { Project, ProjectInput } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";

const projectTypes = ["General", "Movie", "Marketing", "Business", "Research", "Education", "Development"] as const;
const typeKey = (type: string) => `project.type.${type.toLowerCase()}`;

export function ProjectForm({ project, onClose, onSubmit }: Readonly<{ project?: Project; onClose: () => void; onSubmit: (input: ProjectInput) => Promise<void> }>) {
  const { t } = useLocale();
  const [name, setName] = useState(project?.name ?? "");
  const [description, setDescription] = useState(project?.description ?? "");
  const [type, setType] = useState(project?.type ?? "General");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");

  async function submit(event: FormEvent) {
    event.preventDefault(); setSaving(true); setError("");
    try { await onSubmit({ name, description: description || undefined, type }); onClose(); }
    catch (caught) { setError(caught instanceof Error ? caught.message : t("projects.saveError")); }
    finally { setSaving(false); }
  }

  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
    <form className="project-form modal-card" onSubmit={submit}>
      <div className="modal-heading"><div><p className="section-eyebrow">{project ? t("projects.editEyebrow") : t("projects.newEyebrow")}</p><h2>{project ? t("projects.editTitle") : t("projects.newTitle")}</h2></div><button type="button" className="modal-close" onClick={onClose} aria-label={t("common.close")}><X size={18} /></button></div>
      <label><span>{t("projects.name")}</span><input required minLength={1} maxLength={160} value={name} onChange={(event) => setName(event.target.value)} autoFocus /></label>
      <label><span>{t("projects.type")}</span><div className="select-shell"><select value={type} onChange={(event) => setType(event.target.value)}>{projectTypes.map((item) => <option key={item} value={item}>{t(typeKey(item))}</option>)}</select><ChevronDown size={15} /></div></label>
      <label><span>{t("projects.description")}</span><textarea maxLength={2000} rows={4} value={description} onChange={(event) => setDescription(event.target.value)} placeholder={t("projects.descriptionPlaceholder")} /></label>
      {error && <div className="form-error" role="alert">{error}</div>}
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>{t("common.cancel")}</button><button className="primary-button" disabled={saving}>{saving ? t("common.saving") : <><Check size={15} /> {project ? t("common.save") : t("projects.create")}</>}</button></div>
    </form>
  </div>;
}
