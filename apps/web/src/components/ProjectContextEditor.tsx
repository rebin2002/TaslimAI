"use client";

import { FormEvent, useEffect, useMemo, useRef, useState } from "react";
import { Check, FileText } from "lucide-react";
import type { Project, ProjectInput } from "@/lib/api";
import { isRevisionConflict, isRevisionUnavailable, ProjectRevisionSession } from "@/lib/projectRevision";
import { useLocale } from "@/components/LocaleProvider";

export function ProjectContextEditor({ project, onSaved, revision }: Readonly<{
  project: Project;
  onSaved: (project: Project) => void;
  /**
   * The revision session for this project. A detail view that also archives or
   * restores the row owns the session, so a status change it made itself can
   * advance the token instead of stranding the draft on a revision the server
   * has already moved past. Mounted without one, the editor opens its own.
   */
  revision?: ProjectRevisionSession;
}>) {
  const { t } = useLocale();
  const [instructions, setInstructions] = useState(project.instructions ?? "");
  const [contextNotes, setContextNotes] = useState(project.contextNotes ?? "");
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState("");
  const [conflict, setConflict] = useState(false);
  // Identity of the project that owns this mounted instance's draft, its save
  // feedback, and any request still in flight. The view stays mounted while the
  // route parameter changes, so a switch of project - and with it of workspace -
  // has to drop the previous project's state rather than display, submit or
  // confirm it as the new project's context.
  const ownerRef = useRef(project.id);
  // One revision session per project. The token it holds belongs to this id and
  // is never carried across a route switch: declaring another project's
  // revision would either be rejected as unusable or, worse, certify a draft
  // that was never built on the row being saved.
  const ownSession = useMemo(() => new ProjectRevisionSession(project.id), [project.id]);
  const session = revision ?? ownSession;

  useEffect(() => {
    if (ownerRef.current === project.id) return;
    ownerRef.current = project.id;
    setInstructions(project.instructions ?? "");
    setContextNotes(project.contextNotes ?? "");
    setSaving(false);
    setSaved(false);
    setError("");
    setConflict(false);
    // Project identity intentionally controls the local draft boundary.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id]);

  // The revision is captured when the member begins editing, so the token
  // describes the copy the draft was written against. A failed capture is not
  // retried here: the save re-captures rather than sending an unfenced write.
  function beginEditing() {
    if (session.heldRevision) return;
    void session.begin().catch(() => setError(t("projects.revisionLoadError")));
  }

  const draft = (): ProjectInput => ({
    name: project.name,
    description: project.description ?? undefined,
    type: project.type,
    instructions: instructions || undefined,
    contextNotes: contextNotes || undefined,
  });

  async function submit(event: FormEvent) {
    event.preventDefault();
    const submittingProjectId = project.id;
    setSaving(true);
    setSaved(false);
    setError("");
    setConflict(false);
    try {
      const updated = await session.save(draft());
      // A reply that lands after the view moved on belongs to the project that has
      // just been left behind, so it is dropped instead of being written into the
      // overview of the project now on screen.
      if (ownerRef.current !== submittingProjectId) return;
      onSaved(updated.project);
      setSaved(true);
    } catch (caught) {
      if (ownerRef.current !== submittingProjectId) return;
      // The fence wrote nothing. Keep the typed text and hand the choice back
      // to the member instead of re-saving against a revision they never saw.
      if (isRevisionConflict(caught)) {
        // The conflict block carries the explanation and the two choices; the
        // generic error line stays free so the message is not shown twice.
        setConflict(true);
      } else if (isRevisionUnavailable(caught)) {
        setError(t("projects.revisionLoadError"));
      } else {
        setError(caught instanceof Error ? caught.message : t("projects.saveError"));
      }
    } finally {
      // The switch above already cleared the saving flag for the incoming project.
      if (ownerRef.current === submittingProjectId) setSaving(false);
    }
  }

  async function reloadLatest() {
    setSaving(true);
    try {
      const latest = await session.reloadLatest();
      if (ownerRef.current !== project.id) return;
      setInstructions(latest.instructions ?? "");
      setContextNotes(latest.contextNotes ?? "");
      setConflict(false);
      setError(t("projects.revisionReloaded"));
      onSaved(latest);
    } catch (caught) {
      if (ownerRef.current !== project.id) return;
      setError(caught instanceof Error ? caught.message : t("projects.loadError"));
    } finally {
      if (ownerRef.current === project.id) setSaving(false);
    }
  }

  async function keepMyDraft() {
    const submittingProjectId = project.id;
    setSaving(true);
    try {
      const updated = await session.saveOverLatest(draft());
      if (ownerRef.current !== submittingProjectId) return;
      onSaved(updated.project);
      setConflict(false);
      setSaved(true);
    } catch (caught) {
      if (ownerRef.current !== submittingProjectId) return;
      setError(caught instanceof Error ? caught.message : t("projects.saveError"));
    } finally {
      if (ownerRef.current === submittingProjectId) setSaving(false);
    }
  }

  return <form className="project-context-card account-card" onSubmit={submit}>
    <div className="card-title"><span className="card-title-icon teal"><FileText size={17} /></span><div><h2>{t("projects.contextTitle")}</h2><p>{t("projects.contextDescription")}</p></div></div>
    <label><span>{t("projects.instructions")}</span><textarea maxLength={4000} rows={4} value={instructions} onChange={(event) => { beginEditing(); setInstructions(event.target.value); }} placeholder={t("projects.instructionsPlaceholder")} /></label>
    <label><span>{t("projects.contextNotes")}</span><textarea maxLength={8000} rows={5} value={contextNotes} onChange={(event) => { beginEditing(); setContextNotes(event.target.value); }} placeholder={t("projects.contextNotesPlaceholder")} /></label>
    {conflict && <div className="revision-conflict" role="alert">
      <p>{t("projects.revisionConflict")}</p>
      <div className="revision-conflict-actions">
        <button type="button" className="secondary-button" onClick={() => { void reloadLatest(); }}>{t("projects.revisionReloadAction")}</button>
        <button type="button" className="primary-button" onClick={() => { void keepMyDraft(); }}>{t("projects.revisionKeepMineAction")}</button>
      </div>
    </div>}
    {error && <div className="form-error" role="alert">{error}</div>}
    {saved && <div className="form-success"><Check size={15} /> {t("projects.contextSaved")}</div>}
    <button className="primary-button" disabled={saving}>{saving ? t("common.saving") : <><Check size={15} /> {t("projects.saveContext")}</>}</button>
  </form>;
}
