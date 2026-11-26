"use client";

import { FormEvent, useEffect, useMemo, useRef, useState } from "react";
import { Check, FileText } from "lucide-react";
import type { Project, ProjectInput } from "@/lib/api";
import { draftBaseOf, isRevisionConflict, isRevisionDiverged, isRevisionUnavailable, ProjectRevisionSession, type ProjectDraftBase } from "@/lib/projectRevision";
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
  // True when this client found the row already differs from the copy on screen,
  // rather than the server refusing a declared revision. The decision is the same,
  // the explanation is not.
  const [diverged, setDiverged] = useState(false);
  // One conflict action at a time: reload and keep-mine both write, and a double
  // click that ran both would let a wildcard overwrite the reload it raced.
  const busy = useRef(false);
  // Identity of the project that owns this mounted instance's draft, its save
  // feedback, and any request still in flight. The view stays mounted while the
  // route parameter changes, so a switch of project - and with it of workspace -
  // has to drop the previous project's state rather than display, submit or
  // confirm it as the new project's context.
  const ownerRef = useRef(project.id);
  // The immutable copy THIS DRAFT was seeded from: the exact content the local
  // fields above were initialized with. It is deliberately not `project`, which is
  // the parent's live copy and is replaced by every other writer's answer. The
  // fence may only be told about the content the member actually typed into, so a
  // sibling form's save, or an archive landing while this draft sits untouched,
  // cannot make a newer row certify these older fields. It moves only where the
  // local fields themselves are re-based: a project switch, a reload, or a write
  // this draft made.
  const seedRef = useRef<ProjectDraftBase>(draftBaseOf(project));
  // One revision session per project. The token it holds belongs to this id and
  // is never carried across a route switch: declaring another project's
  // revision would either be rejected as unusable or, worse, certify a draft
  // that was never built on the row being saved.
  const ownSession = useMemo(() => new ProjectRevisionSession(project.id, "context"), [project.id]);
  const session = revision ?? ownSession;
  // The session this mounted draft is speaking for right now. A capture issued for
  // an earlier project - or an earlier session object for the same id - may answer
  // after the switch, and its verdict belongs to the row that was left behind.
  const activeSessionRef = useRef(session);

  useEffect(() => {
    if (ownerRef.current !== project.id) {
      ownerRef.current = project.id;
      setInstructions(project.instructions ?? "");
      setContextNotes(project.contextNotes ?? "");
      setSaving(false);
      setSaved(false);
      setError("");
      setConflict(false);
      setDiverged(false);
      busy.current = false;
      // The draft now describes a different row, so its seed moves with it.
      seedRef.current = draftBaseOf(project);
    }
    // Tell the session what is on screen: once per draft, not once per parent
    // render. After this point only the paths below re-base it.
    session.noteDraft(seedRef.current);
    activeSessionRef.current = session;
    // Project identity intentionally controls the local draft boundary.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id, session]);

  // The revision is captured when the member begins editing, so the token
  // describes the copy the draft was written against. A failed capture is not
  // retried here: the save re-captures rather than sending an unfenced write.
  function beginEditing() {
    if (session.isBound || conflict) return;
    const startedForProject = project.id;
    const startedForSession = session;
    // The token is only ever bound to the content it was published for, so this
    // read compares the row against the copy this draft was seeded from - never the
    // parent's live prop, which may already describe a newer row that this textarea
    // never displayed. A draft that is out of date reports a conflict instead of
    // stamping its stale fields with the newer revision.
    void session.begin().catch((caught) => {
      // Late answer: the draft moved on while this read was in flight. Reporting a
      // conflict or an error here would put the previous project's verdict on the
      // page the member is now looking at, which is both wrong and unfalsifiable
      // to them. The re-base above already cleared these flags for the new row.
      if (ownerRef.current !== startedForProject || activeSessionRef.current !== startedForSession) return;
      if (isRevisionConflict(caught)) {
        setDiverged(isRevisionDiverged(caught));
        setConflict(true);
      } else {
        setError(t("projects.revisionLoadError"));
      }
    });
  }

  // The payload is the seeded copy plus this draft's own edits. Taking the
  // untouched fields from `seedRef` rather than from `project` means a field some
  // other writer moved can never ride along into this write: the fence refuses the
  // write instead, and the member decides what happens to their text.
  const draft = (): ProjectInput => ({
    name: seedRef.current.name,
    description: seedRef.current.description ?? undefined,
    type: seedRef.current.type,
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
      // This draft wrote the row, so what is on screen and what the token certifies
      // are now the same content.
      seedRef.current = draftBaseOf(updated.project);
      onSaved(updated.project);
      setSaved(true);
    } catch (caught) {
      if (ownerRef.current !== submittingProjectId) return;
      // The fence wrote nothing. Keep the typed text and hand the choice back
      // to the member instead of re-saving against a revision they never saw.
      if (isRevisionConflict(caught)) {
        // The conflict block carries the explanation and the two choices; the
        // generic error line stays free so the message is not shown twice.
        setDiverged(isRevisionDiverged(caught));
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
    if (busy.current) return;
    busy.current = true;
    setSaving(true);
    try {
      const latest = await session.reloadLatest();
      if (ownerRef.current !== project.id) return;
      setInstructions(latest.instructions ?? "");
      setContextNotes(latest.contextNotes ?? "");
      // Re-based onto the server's copy: these fields are now the content the
      // reloaded token was published for.
      seedRef.current = draftBaseOf(latest);
      setConflict(false);
      setDiverged(false);
      setError(t("projects.revisionReloaded"));
      onSaved(latest);
    } catch (caught) {
      if (ownerRef.current !== project.id) return;
      setError(caught instanceof Error ? caught.message : t("projects.loadError"));
    } finally {
      busy.current = false;
      if (ownerRef.current === project.id) setSaving(false);
    }
  }

  async function keepMyDraft() {
    const submittingProjectId = project.id;
    if (busy.current) return;
    busy.current = true;
    setSaving(true);
    try {
      const updated = await session.saveOverLatest(draft());
      if (ownerRef.current !== submittingProjectId) return;
      seedRef.current = draftBaseOf(updated.project);
      onSaved(updated.project);
      setConflict(false);
      setDiverged(false);
      setSaved(true);
    } catch (caught) {
      if (ownerRef.current !== submittingProjectId) return;
      setError(caught instanceof Error ? caught.message : t("projects.saveError"));
    } finally {
      busy.current = false;
      if (ownerRef.current === submittingProjectId) setSaving(false);
    }
  }

  return <form className="project-context-card account-card" onSubmit={submit}>
    <div className="card-title"><span className="card-title-icon teal"><FileText size={17} /></span><div><h2>{t("projects.contextTitle")}</h2><p>{t("projects.contextDescription")}</p></div></div>
    <label><span>{t("projects.instructions")}</span><textarea maxLength={4000} rows={4} value={instructions} onChange={(event) => { beginEditing(); setInstructions(event.target.value); }} placeholder={t("projects.instructionsPlaceholder")} /></label>
    <label><span>{t("projects.contextNotes")}</span><textarea maxLength={8000} rows={5} value={contextNotes} onChange={(event) => { beginEditing(); setContextNotes(event.target.value); }} placeholder={t("projects.contextNotesPlaceholder")} /></label>
    {conflict && <div className="revision-conflict" role="alert">
      <p>{t(diverged ? "projects.revisionDiverged" : "projects.revisionConflict")}</p>
      <div className="revision-conflict-actions">
        <button type="button" className="secondary-button" disabled={saving} onClick={() => { void reloadLatest(); }}>{t("projects.revisionReloadAction")}</button>
        <button type="button" className="primary-button" disabled={saving} onClick={() => { void keepMyDraft(); }}>{t("projects.revisionKeepMineAction")}</button>
      </div>
    </div>}
    {error && <div className="form-error" role="alert">{error}</div>}
    {saved && <div className="form-success"><Check size={15} /> {t("projects.contextSaved")}</div>}
    <button className="primary-button" disabled={saving}>{saving ? t("common.saving") : <><Check size={15} /> {t("projects.saveContext")}</>}</button>
  </form>;
}
