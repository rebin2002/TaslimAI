"use client";

import Link from "next/link";
import { FormEvent, useEffect, useState } from "react";
import {
  BarChart3,
  Brain,
  Check,
  ChevronRight,
  Clock3,
  CreditCard,
  FileKey2,
  Globe2,
  KeyRound,
  Languages,
  LogOut,
  Mail,
  ShieldCheck,
  SlidersHorizontal,
  UserRound,
  UsersRound,
} from "lucide-react";
import { useAuth } from "@/components/AuthProvider";
import { localeNames, locales, useLocale } from "@/components/LocaleProvider";
import { api, type PasswordPolicy } from "@/lib/api";
import type { Locale } from "@/lib/i18n";

const timeZones = [
  "UTC",
  "Asia/Baghdad",
  "Asia/Erbil",
  "Asia/Riyadh",
  "Europe/London",
  "Europe/Berlin",
  "America/New_York",
  "America/Los_Angeles",
];

const outputPreferences = ["concise", "balanced", "detailed"] as const;

type SaveState = "idle" | "saving" | "saved";

export function AccountView() {
  const { user, workspace, updateProfile, signOut } = useAuth();
  const { t, locale } = useLocale();
  const [displayName, setDisplayName] = useState(user?.displayName ?? "");
  const [preferredLanguage, setPreferredLanguage] = useState<Locale>(user?.preferredLanguage ?? "en");
  const [defaultGenerationLanguage, setDefaultGenerationLanguage] = useState<Locale>(user?.defaultGenerationLanguage ?? "en");
  const [timeZone, setTimeZone] = useState(user?.timeZone ?? "UTC");
  const [outputPreference, setOutputPreference] = useState<(typeof outputPreferences)[number]>(user?.outputPreference ?? "balanced");
  const [includeSourceLinks, setIncludeSourceLinks] = useState(user?.includeSourceLinks ?? true);
  const [profileState, setProfileState] = useState<SaveState>("idle");
  const [profileError, setProfileError] = useState("");
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [passwordState, setPasswordState] = useState<SaveState>("idle");
  const [passwordError, setPasswordError] = useState("");
  const [passwordPolicy, setPasswordPolicy] = useState<PasswordPolicy | null>(null);
  const [policyError, setPolicyError] = useState(false);

  useEffect(() => {
    let active = true;
    void api.passwordPolicy()
      .then((policy) => { if (active) setPasswordPolicy(policy); })
      .catch(() => { if (active) setPolicyError(true); });
    return () => { active = false; };
  }, []);

  const createdDate = user?.createdAt ? new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(user.createdAt)) : "—";
  const initials = user?.displayName.trim().slice(0, 1).toUpperCase() || "T";

  async function saveProfile(event: FormEvent) {
    event.preventDefault();
    setProfileState("saving");
    setProfileError("");
    try {
      await updateProfile({ displayName, preferredLanguage, defaultGenerationLanguage, timeZone, outputPreference, includeSourceLinks });
      setProfileState("saved");
    } catch (caught) {
      setProfileState("idle");
      setProfileError(caught instanceof Error ? caught.message : t("account.saveError"));
    }
  }

  async function changePassword(event: FormEvent) {
    event.preventDefault();
    setPasswordError("");
    if (newPassword !== confirmPassword) {
      setPasswordError(t("auth.passwordMismatch"));
      return;
    }
    setPasswordState("saving");
    try {
      await api.changePassword({ currentPassword, newPassword });
      setCurrentPassword("");
      setNewPassword("");
      setConfirmPassword("");
      setPasswordState("saved");
    } catch (caught) {
      setPasswordState("idle");
      setPasswordError(caught instanceof Error ? caught.message : t("account.passwordError"));
    }
  }

  return (
    <div className="account-page account-settings-page">
      <div className="account-header account-settings-hero">
        <div>
          <p className="section-eyebrow">{t("account.eyebrow")}</p>
          <h1>{t("account.title")}</h1>
          <p>{t("account.subtitle")}</p>
        </div>
        <div className="account-identity-mark"><span className="account-avatar">{initials}</span><div><strong>{user?.displayName}</strong><span>{user?.email}</span></div></div>
      </div>

      <div className="account-settings-grid">
        <form className="account-card profile-form account-profile-card" onSubmit={saveProfile}>
          <div className="card-title"><span className="card-title-icon"><UserRound size={17} /></span><div><h2>{t("account.profile")}</h2><p>{t("account.profileSubtitle")}</p></div></div>
          <label><span>{t("auth.displayName")}</span><div className="input-shell"><UserRound size={16} /><input required minLength={2} maxLength={120} value={displayName} onChange={(event) => setDisplayName(event.target.value)} /></div></label>
          <label><span>{t("auth.email")}</span><div className="input-shell is-disabled"><Mail size={16} /><input value={user?.email ?? ""} readOnly /></div><small className="field-hint">{t("account.emailHint")}</small></label>
          <div className="account-form-divider" />
          <div className="card-subheading"><Languages size={15} /><span>{t("account.preferences")}</span></div>
          <div className="account-control-grid">
            <label><span>{t("account.interfaceLanguage")}</span><div className="select-shell"><Globe2 size={16} /><select value={preferredLanguage} onChange={(event) => setPreferredLanguage(event.target.value as Locale)}>{locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}</select></div></label>
            <label><span>{t("account.defaultGenerationLanguage")}</span><div className="select-shell"><Languages size={16} /><select value={defaultGenerationLanguage} onChange={(event) => setDefaultGenerationLanguage(event.target.value as Locale)}>{locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}</select></div></label>
            <label><span>{t("account.timezone")}</span><div className="select-shell"><Clock3 size={16} /><select value={timeZone} onChange={(event) => setTimeZone(event.target.value)}>{!timeZones.includes(timeZone) && <option value={timeZone}>{timeZone}</option>}{timeZones.map((item) => <option key={item} value={item}>{item}</option>)}</select></div></label>
            <label><span>{t("account.outputPreference")}</span><div className="select-shell"><SlidersHorizontal size={16} /><select value={outputPreference} onChange={(event) => setOutputPreference(event.target.value as typeof outputPreferences[number])}>{outputPreferences.map((item) => <option key={item} value={item}>{t(`account.output.${item}`)}</option>)}</select></div></label>
          </div>
          <label className="account-check-row"><input type="checkbox" checked={includeSourceLinks} onChange={(event) => setIncludeSourceLinks(event.target.checked)} /><span><strong>{t("account.includeSourceLinks")}</strong><small>{t("account.includeSourceLinksHint")}</small></span></label>
          {profileError && <div className="form-error" role="alert">{profileError}</div>}
          {profileState === "saved" && <div className="form-success"><Check size={15} /> {t("account.saved")}</div>}
          <button className="primary-button" disabled={profileState === "saving"}>{profileState === "saving" ? t("common.saving") : <><Check size={15} /> {t("common.saveChanges")}</>}</button>
        </form>

        <div className="account-side">
          <div className="account-card account-info-card">
            <div className="card-title"><span className="card-title-icon teal"><UserRound size={17} /></span><div><h2>{t("account.information")}</h2><p>{t("account.informationSubtitle")}</p></div></div>
            <dl className="account-definition-list"><div><dt>{t("account.accountId")}</dt><dd>{user?.id.slice(0, 8)}…</dd></div><div><dt>{t("account.created")}</dt><dd>{createdDate}</dd></div><div><dt>{t("account.access")}</dt><dd>{t("account.authenticatedAccess")}</dd></div></dl>
          </div>
          <div className="account-card workspace-card-detail">
            <div className="card-title"><span className="card-title-icon teal"><UsersRound size={17} /></span><div><h2>{t("account.workspace")}</h2><p>{t("account.workspaceSubtitle")}</p></div></div>
            <strong>{workspace?.name}</strong><span className="workspace-role-badge">{workspace?.role === "Owner" ? t("account.owner") : workspace?.role}</span>
            <div className="workspace-future-row"><span>{t("account.workspaceRole")}</span><span>{workspace?.role}</span></div>
            <div className="workspace-future-note"><UsersRound size={14} /><span>{t("account.workspaceFuture")}</span></div>
          </div>
        </div>
      </div>

      <div className="account-section-heading"><div><p className="section-eyebrow">{t("account.securityEyebrow")}</p><h2>{t("account.securityTitle")}</h2></div><ShieldCheck size={22} /></div>
      <div className="account-settings-grid security-grid">
        <form className="account-card profile-form" onSubmit={changePassword}>
          <div className="card-title"><span className="card-title-icon"><KeyRound size={17} /></span><div><h2>{t("account.changePassword")}</h2><p>{t("account.changePasswordSubtitle")}</p></div></div>
          <label><span>{t("account.currentPassword")}</span><div className="input-shell"><KeyRound size={16} /><input type="password" required autoComplete="current-password" value={currentPassword} onChange={(event) => setCurrentPassword(event.target.value)} /></div></label>
          <label><span>{t("account.newPassword")}</span><div className="input-shell"><KeyRound size={16} /><input type="password" required autoComplete="new-password" value={newPassword} onChange={(event) => setNewPassword(event.target.value)} /></div></label>
          <label><span>{t("auth.confirmPassword")}</span><div className="input-shell"><KeyRound size={16} /><input type="password" required autoComplete="new-password" value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} /></div></label>
          <p className="field-hint">{passwordPolicy ? [passwordPolicy.requiredLength && t("auth.passwordRequirementLength", { count: String(passwordPolicy.requiredLength) }), passwordPolicy.requireUppercase && t("auth.passwordRequirementUppercase"), passwordPolicy.requireLowercase && t("auth.passwordRequirementLowercase"), passwordPolicy.requireDigit && t("auth.passwordRequirementDigit"), passwordPolicy.requireNonAlphanumeric && t("auth.passwordRequirementSymbol")].filter(Boolean).join(" · ") : policyError ? t("account.passwordPolicyUnavailable") : t("account.passwordPolicyLoading")}</p>
          {passwordError && <div className="form-error" role="alert">{passwordError}</div>}
          {passwordState === "saved" && <div className="form-success"><Check size={15} /> {t("account.passwordSaved")}</div>}
          <button className="primary-button" disabled={passwordState === "saving"}>{passwordState === "saving" ? t("common.saving") : <><KeyRound size={15} /> {t("account.updatePassword")}</>}</button>
        </form>
        <div className="account-card security-summary-card"><div className="card-title"><span className="card-title-icon teal"><ShieldCheck size={17} /></span><div><h2>{t("account.sessionSecurity")}</h2><p>{t("account.sessionSecuritySubtitle")}</p></div></div><div className="security-point"><ShieldCheck size={15} /><span>{t("account.httpOnlySession")}</span></div><div className="security-point"><FileKey2 size={15} /><span>{t("account.csrfProtected")}</span></div><div className="security-point"><LogOut size={15} /><span>{t("account.currentSessionOnly")}</span></div><button className="secondary-button security-logout" onClick={() => void signOut()}><LogOut size={15} /> {t("auth.logout")}</button></div>
      </div>

      <div className="account-section-heading"><div><p className="section-eyebrow">{t("account.contextEyebrow")}</p><h2>{t("account.contextTitle")}</h2></div><Brain size={22} /></div>
      <div className="account-context-grid">
        <div className="account-card context-card memory-context-card"><div className="card-title"><span className="card-title-icon"><Brain size={17} /></span><div><h2>{t("account.memoryTitle")}</h2><p>{t("account.memorySubtitle")}</p></div></div><p>{t("account.memoryExplanation")}</p><Link className="secondary-button" href="/account/memory">{t("account.viewMemory")} <ChevronRight size={15} /></Link></div>
        <div className="account-card context-card"><div className="card-title"><span className="card-title-icon teal"><BarChart3 size={17} /></span><div><h2>{t("account.historyTitle")}</h2><p>{t("account.historySubtitle")}</p></div></div><p>{t("account.historyExplanation")}</p><Link className="secondary-button" href="/chat">{t("account.viewHistory")} <ChevronRight size={15} /></Link></div>
        <div className="account-card context-card"><div className="card-title"><span className="card-title-icon"><FileKey2 size={17} /></span><div><h2>{t("account.projectContextTitle")}</h2><p>{t("account.projectContextSubtitle")}</p></div></div><p>{t("account.projectContextExplanation")}</p><Link className="secondary-button" href="/projects">{t("account.viewProjects")} <ChevronRight size={15} /></Link></div>
      </div>

      <div className="account-section-heading"><div><p className="section-eyebrow">{t("account.resourcesEyebrow")}</p><h2>{t("account.resourcesTitle")}</h2></div></div>
      <div className="account-resource-grid">
        <div className="account-card usage-link-card"><div><h2>{t("account.usageTitle")}</h2><p>{t("account.usageSubtitle")}</p></div><Link className="secondary-button" href="/account/usage"><BarChart3 size={15} /> {t("account.viewUsage")}</Link></div>
        <div className="account-card usage-link-card"><div><h2>{t("account.billingTitle")}</h2><p>{t("account.billingSubtitle")}</p></div><Link className="secondary-button" href="/account/billing"><CreditCard size={15} /> {t("account.viewBilling")}</Link></div>
        <div className="account-card privacy-card"><div className="card-title"><span className="card-title-icon teal"><FileKey2 size={17} /></span><div><h2>{t("account.privacyTitle")}</h2><p>{t("account.privacySubtitle")}</p></div></div><p>{t("account.privacyExplanation")}</p></div>
      </div>
    </div>
  );
}
