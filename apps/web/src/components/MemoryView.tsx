"use client";

import { FormEvent, useEffect, useState } from "react";
import { Check, Pencil, Plus, Trash2, X } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { useLocale } from "@/components/LocaleProvider";
import { api, type PersonalMemory } from "@/lib/api";

const categories = ["Preference", "Personal", "Business", "Writing", "Language", "Other"] as const;

export function MemoryView() {
  const { workspace } = useAuth();
  const { t } = useLocale();
  const [memories, setMemories] = useState<PersonalMemory[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [editing, setEditing] = useState<PersonalMemory | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const [category, setCategory] = useState("Preference");
  const [title, setTitle] = useState("");
  const [content, setContent] = useState("");
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!workspace?.id) return;
    let active = true;
    void api.listMemories(workspace.id)
      .then((result) => { if (active) setMemories(result); })
      .catch((caught) => { if (active) setError(caught instanceof Error ? caught.message : t("memory.loadError")); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [workspace?.id, t]);

  function openCreate() {
    setEditing(null); setCategory("Preference"); setTitle(""); setContent(""); setFormOpen(true); setError("");
  }

  function openEdit(memory: PersonalMemory) {
    setEditing(memory); setCategory(memory.category); setTitle(memory.title); setContent(memory.content); setFormOpen(true); setError("");
  }

  function closeForm() { setFormOpen(false); setEditing(null); }

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!workspace?.id) return;
    setSaving(true); setError("");
    try {
      const input = { category, title, content };
      const result = editing ? await api.updateMemory(editing.id, input) : await api.createMemory(workspace.id, input);
      setMemories((items) => editing ? items.map((item) => item.id === editing.id ? result : item) : [result, ...items]);
      closeForm();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : t("memory.saveError"));
    } finally { setSaving(false); }
  }

  async function remove(memory: PersonalMemory) {
    if (!window.confirm(t("memory.deleteConfirm"))) return;
    try { await api.deleteMemory(memory.id); setMemories((items) => items.filter((item) => item.id !== memory.id)); }
    catch (caught) { setError(caught instanceof Error ? caught.message : t("memory.deleteError")); }
  }

  return <div className="account-page memory-page">
    <div className="account-header"><div><p className="section-eyebrow">{t("memory.eyebrow")}</p><h1>{t("memory.title")}</h1><p>{t("memory.subtitle")}</p></div><button className="primary-button" onClick={openCreate}><Plus size={15} /> {t("memory.add")}</button></div>
    <div className="memory-notice">{t("memory.notice")}</div>
    {error && <div className="form-error" role="alert">{error}</div>}
    {loading && <div className="account-card usage-loading">{t("memory.loading")}</div>}
    {!loading && !memories.length && <div className="account-card memory-empty"><h2>{t("memory.emptyTitle")}</h2><p>{t("memory.emptyDescription")}</p><button className="secondary-button" onClick={openCreate}><Plus size={15} /> {t("memory.add")}</button></div>}
    {!!memories.length && <div className="memory-list">{memories.map((memory) => <article className="account-card memory-card" key={memory.id}><div className="memory-card-header"><div><span className="memory-category">{t(`memory.category.${memory.category.toLowerCase()}`)}</span><h2>{memory.title}</h2></div><div className="memory-actions"><button className="icon-button" onClick={() => openEdit(memory)} aria-label={t("memory.edit")}><Pencil size={15} /></button><button className="icon-button danger-icon" onClick={() => void remove(memory)} aria-label={t("memory.delete")}><Trash2 size={15} /></button></div></div><p>{memory.content}</p></article>)}</div>}
    {formOpen && <div className="modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) closeForm(); }}><form className="memory-form modal-card" onSubmit={submit}><div className="modal-heading"><div><p className="section-eyebrow">{editing ? t("memory.editEyebrow") : t("memory.addEyebrow")}</p><h2>{editing ? t("memory.editTitle") : t("memory.addTitle")}</h2></div><button type="button" className="modal-close" onClick={closeForm} aria-label={t("common.close")}><X size={18} /></button></div><label><span>{t("memory.category")}</span><select value={category} onChange={(event) => setCategory(event.target.value)}>{categories.map((item) => <option key={item} value={item}>{t(`memory.category.${item.toLowerCase()}`)}</option>)}</select></label><label><span>{t("memory.name")}</span><input required minLength={1} maxLength={160} value={title} onChange={(event) => setTitle(event.target.value)} /></label><label><span>{t("memory.content")}</span><textarea required minLength={1} maxLength={4000} rows={6} value={content} onChange={(event) => setContent(event.target.value)} placeholder={t("memory.contentPlaceholder")} /></label><p className="field-hint">{t("memory.manualOnly")}</p><div className="modal-actions"><button type="button" className="secondary-button" onClick={closeForm}>{t("common.cancel")}</button><button className="primary-button" disabled={saving}>{saving ? t("common.saving") : <><Check size={15} /> {t("common.save")}</>}</button></div></form></div>}
  </div>;
}
