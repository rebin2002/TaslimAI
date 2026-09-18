"use client";

import { FormEvent, useState } from "react";
import { Check, Languages, LogOut, Mail, UserRound } from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { localeNames, locales, useLocale } from "@/components/LocaleProvider";
import type { Locale } from "@/lib/i18n";

export function AccountView() {
  const { user, workspace, updateProfile, signOut } = useAuth();
  const { t } = useLocale();
  const [displayName, setDisplayName] = useState(user?.displayName ?? "");
  const [preferredLanguage, setPreferredLanguage] = useState<Locale>(user?.preferredLanguage ?? "en");
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState("");
  async function submit(event: FormEvent) { event.preventDefault(); setSaving(true); setSaved(false); setError(""); try { await updateProfile({ displayName, preferredLanguage }); setSaved(true); } catch (caught) { setError(caught instanceof Error ? caught.message : t("account.saveError")); } finally { setSaving(false); } }
  return <div className="account-page"><div className="account-header"><div><p className="section-eyebrow">{t("account.eyebrow")}</p><h1>{t("account.title")}</h1><p>{t("account.subtitle")}</p></div><span className="account-avatar">{user?.displayName.slice(0, 1).toUpperCase()}</span></div><div className="account-grid"><form className="account-card profile-form" onSubmit={submit}><div className="card-title"><span className="card-title-icon"><UserRound size={17} /></span><div><h2>{t("account.profile")}</h2><p>{t("account.profileSubtitle")}</p></div></div><label><span>{t("auth.displayName")}</span><div className="input-shell"><UserRound size={16} /><input required minLength={2} maxLength={120} value={displayName} onChange={(event) => setDisplayName(event.target.value)} /></div></label><label><span>{t("auth.email")}</span><div className="input-shell is-disabled"><Mail size={16} /><input value={user?.email ?? ""} readOnly /></div><small className="field-hint">{t("account.emailHint")}</small></label><label><span>{t("account.preferredLanguage")}</span><div className="select-shell"><Languages size={16} /><select value={preferredLanguage} onChange={(event) => setPreferredLanguage(event.target.value as Locale)}>{locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}</select></div></label>{error && <div className="form-error">{error}</div>}{saved && <div className="form-success"><Check size={15} /> {t("account.saved")}</div>}<button className="primary-button" disabled={saving}>{saving ? t("common.saving") : <><Check size={15} /> {t("common.saveChanges")}</>}</button></form><div className="account-side"><div className="account-card workspace-card-detail"><div className="card-title"><span className="card-title-icon teal"><UserRound size={17} /></span><div><h2>{t("account.workspace")}</h2><p>{t("account.workspaceSubtitle")}</p></div></div><strong>{workspace?.name}</strong><span>{t("account.owner")}</span></div><div className="account-card danger-card"><div><h2>{t("account.session")}</h2><p>{t("account.sessionSubtitle")}</p></div><button className="secondary-button" onClick={() => void signOut()}><LogOut size={15} /> {t("auth.logout")}</button></div></div></div></div>;
}
