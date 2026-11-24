"use client";

import { FormEvent, useEffect, useRef, useState } from "react";
import { Check, ChevronDown, X } from "lucide-react";
import type { Project, ProjectInput } from "@/lib/api";
import { draftBaseOf, isRevisionConflict, isRevisionDiverged, isRevisionUnavailable, type ProjectDraftBase, type ProjectRevisionSession } from "@/lib/projectRevision";
import { useLocale } from "@/components/LocaleProvider";
import { localeDirection } from "@/lib/i18n";

const projectTypes = ["General", "Movie", "Marketing", "Business", "Research", "Education", "Development"] as const;
const typeKey = (type: string) => `project.type.${type.toLowerCase()}`;

export function ProjectForm({ project, onClose, onSubmit, revision, onSaved }: Readonly<{
  project?: Project;
  onClose: () => void;
  /** Create path: the caller owns the write. Omitted when `revision` fences an edit. */
  onSubmit?: (input: ProjectInput) => Promise<void>;
  /**
   * Edit path only. When present the form writes through the revision fence:
   * the token is captured as soon as the member starts typing, every save
   * declares it, and a rejected revision keeps the form open with the draft
   * intact instead of overwriting the newer server copy. Creation has no
   * revision to declare and keeps using `onSubmit`.
   */
  revision?: ProjectRevisionSession | null;
  /** The project the server committed, for the edit path to fold into its view. */
  onSaved?: (project: Project) => void;
}>) {
  const { t, locale } = useLocale();
  const [name, setName] = useState(project?.name ?? "");
  const [description, setDescription] = useState(project?.description ?? "");
  const [instructions, setInstructions] = useState(project?.instructions ?? "");
  const [contextNotes, setContextNotes] = useState(project?.contextNotes ?? "");
  const [type, setType] = useState(project?.type ?? "General");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  // The fence refused the revision this draft was built on. The draft stays
  // exactly as typed until the member chooses what happens to it.
  const [conflict, setConflict] = useState(false);
  const [diverged, setDiverged] = useState(false);
  // Reload and keep-mine both write: a double click that ran them together would
  // let the wildcard overwrite the reload it raced.
  const busy = useRef(false);
  // The immutable copy THIS DRAFT was seeded from, i.e. the row these fields were
  // initialized with. It is not `project`: the parent may replace that prop with a
  // newer answer from another form or a status write, and a copy the member never
  // typed against cannot certify their text. Only a re-seed of the local fields - a
  // different project, an explicit reload, or a write this form made - moves it.
  const seedRef = useRef<ProjectDraftBase | null>(project ? draftBaseOf(project) : null);
  const seededFrom = useRef(project?.id);
  const projectId = project?.id;

  useEffect(() => {
    if (seededFrom.current !== projectId) {
      // A different row: the fields belong to it now, so the seed follows them.
      seededFrom.current = projectId;
      setName(project?.name ?? "");
      setDescription(project?.description ?? "");
      setInstructions(project?.instructions ?? "");
      setContextNotes(project?.contextNotes ?? "");
      setType(project?.type ?? "General");
      setConflict(false);
      setDiverged(false);
      setError("");
      busy.current = false;
      seedRef.current = project ? draftBaseOf(project) : null;
    }
    // Report the copy on screen once per draft, never once per parent render: a
    // newer prop arriving from elsewhere must not re-certify these older fields.
    if (revision && seedRef.current) revision.noteDraft(seedRef.current);
    // The row's identity, not its content, bounds the draft: `project` changing
    // underneath an open form is another writer's answer, not a re-seed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [revision, projectId]);

  const draft = (): ProjectInput => ({ name, description: description || undefined, instructions: instructions || undefined, contextNotes: contextNotes || undefined, type });

  // Capturing at the first keystroke is what makes the declared revision the
  // revision the draft was actually built on. A capture that fails is not
  // retried here: the save re-captures rather than sending an unfenced write.
  function beginEditing() {
    if (!revision || !project || revision.isBound || conflict) return;
    // Bound to the content being edited, never to whatever stamp the row happens
    // to carry now: an out-of-date form reports a conflict instead of stamping
    // its stale draft with a revision it did not edit from.
    void revision.begin().catch((caught) => {
      if (isRevisionConflict(caught)) {
        setDiverged(isRevisionDiverged(caught));
        setConflict(true);
      } else {
        setError(t("projects.revisionLoadError"));
      }
    });
  }

  async function submit(event: FormEvent) {
    event.preventDefault(); setSaving(true); setError(""); setConflict(false);
    setDiverged(false);
    try {
      if (revision && project) {
        const saved = await revision.save(draft());
        // This form wrote the row, so the screen and the certified content match.
        seedRef.current = draftBaseOf(saved.project);
        onSaved?.(saved.project);
      }
      else if (revision) throw new Error("An edit form needs the project it edits.");
      else await onSubmit?.(draft());
      onClose();
    }
    catch (caught) {
      if (revision && isRevisionConflict(caught)) {
        // Nothing was written. Keep the draft, keep the expired token, and ask
        // the member which copy should win; never re-declare a newer revision
        // behind their back.
        setDiverged(isRevisionDiverged(caught));
        setConflict(true);
      } else if (isRevisionUnavailable(caught)) {
        setError(t("projects.revisionLoadError"));
      } else {
        setError(caught instanceof Error ? caught.message : t("projects.saveError"));
      }
    }
    finally { setSaving(false); }
  }

  async function reloadLatest() {
    if (!revision || busy.current) return;
    busy.current = true;
    setSaving(true);
    try {
      const latest = await revision.reloadLatest();
      setName(latest.name);
      setDescription(latest.description ?? "");
      setInstructions(latest.instructions ?? "");
      setContextNotes(latest.contextNotes ?? "");
      setType(latest.type);
      // Re-based onto the server's copy, which is what the reloaded token certifies.
      seedRef.current = draftBaseOf(latest);
      setConflict(false);
      setDiverged(false);
      setError(t("projects.revisionReloaded"));
      onSaved?.(latest);
    }
    catch (caught) { setError(caught instanceof Error ? caught.message : t("projects.loadError")); }
    finally { busy.current = false; setSaving(false); }
  }

  async function keepMyDraft() {
    if (!revision || busy.current) return;
    busy.current = true;
    setSaving(true);
    try {
      const saved = await revision.saveOverLatest(draft());
      seedRef.current = draftBaseOf(saved.project);
      setConflict(false);
      setDiverged(false);
      onSaved?.(saved.project);
      onClose();
    }
    catch (caught) { setError(caught instanceof Error ? caught.message : t("projects.saveError")); }
    finally { busy.current = false; setSaving(false); }
  }

  return <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
    <form className="project-form modal-card" onSubmit={submit}>
      <div className="modal-heading"><div><p className="section-eyebrow">{project ? t("projects.editEyebrow") : t("projects.newEyebrow")}</p><h2>{project ? t("projects.editTitle") : t("projects.newTitle")}</h2></div><button type="button" className="modal-close" onClick={onClose} aria-label={t("common.close")}><X size={18} /></button></div>
      <label><span>{t("projects.name")}</span><input required minLength={1} maxLength={160} value={name} onChange={(event) => { beginEditing(); setName(event.target.value); }} autoFocus dir="auto" /></label>
      <label><span>{t("projects.type")}</span><div className="select-shell"><select value={type} onChange={(event) => { beginEditing(); setType(event.target.value); }} dir={localeDirection(locale)}>{projectTypes.map((item) => <option key={item} value={item}>{t(typeKey(item))}</option>)}</select><ChevronDown size={15} aria-hidden="true" /></div></label>
      <label><span>{t("projects.description")}</span><textarea maxLength={2000} rows={4} value={description} onChange={(event) => { beginEditing(); setDescription(event.target.value); }} placeholder={t("projects.descriptionPlaceholder")} dir="auto" /></label>
      <label><span>{t("projects.instructions")}</span><textarea maxLength={4000} rows={4} value={instructions} onChange={(event) => { beginEditing(); setInstructions(event.target.value); }} placeholder={t("projects.instructionsPlaceholder")} dir="auto" /></label>
      <label><span>{t("projects.contextNotes")}</span><textarea maxLength={8000} rows={5} value={contextNotes} onChange={(event) => { beginEditing(); setContextNotes(event.target.value); }} placeholder={t("projects.contextNotesPlaceholder")} dir="auto" /></label>
      {conflict && <div className="revision-conflict" role="alert">
        <p>{t(diverged ? "projects.revisionDiverged" : "projects.revisionConflict")}</p>
        <div className="revision-conflict-actions">
          <button type="button" className="secondary-button" disabled={saving} onClick={() => { void reloadLatest(); }}>{t("projects.revisionReloadAction")}</button>
          <button type="button" className="primary-button" disabled={saving} onClick={() => { void keepMyDraft(); }}>{t("projects.revisionKeepMineAction")}</button>
        </div>
      </div>}
      {error && <div className="form-error" role="alert">{error}</div>}
      <div className="modal-actions"><button type="button" className="secondary-button" onClick={onClose}>{t("common.cancel")}</button><button className="primary-button" disabled={saving}>{saving ? t("common.saving") : <><Check size={15} /> {project ? t("common.save") : t("projects.create")}</>}</button></div>
    </form>
  </div>;
}
