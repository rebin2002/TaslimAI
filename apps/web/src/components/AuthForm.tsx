"use client";

import Link from "next/link";
import { FormEvent, useEffect, useMemo, useState } from "react";
import { ArrowLeft, ArrowRight, Check, CircleAlert, Eye, EyeOff, Languages, LockKeyhole, Mail, UserRound } from "lucide-react";
import { useLocale, localeNames, locales } from "@/components/LocaleProvider";
import { useAuth } from "@/components/AuthProvider";
import { api, ApiError, type PasswordPolicy } from "@/lib/api";
import { getAuthErrorTranslationKey } from "@/lib/authErrors";
import { BrandMark } from "@/components/BrandMark";
import { localeDirection } from "@/lib/i18n";

type PasswordRequirement = { code: string; label: string; satisfied: boolean };

// This mirrors the server's configured IdentityOptions.Password values only for
// the first paint. The API policy endpoint replaces it as soon as it responds.
const initialPasswordPolicy: PasswordPolicy = {
  requiredLength: 10,
  requireUppercase: true,
  requireLowercase: true,
  requireDigit: true,
  requireNonAlphanumeric: true,
  requiredUniqueChars: 1,
};

export function AuthForm({ mode }: Readonly<{ mode: "login" | "register" }>) {
  const { t, locale, setLocale } = useLocale();
  const { signIn, register } = useAuth();
  const [displayName, setDisplayName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [passwordPolicy, setPasswordPolicy] = useState<PasswordPolicy>(initialPasswordPolicy);
  const [error, setError] = useState("");
  const [passwordErrors, setPasswordErrors] = useState<string[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const isRegister = mode === "register";

  useEffect(() => {
    let active = true;
    void api.passwordPolicy().then((policy) => { if (active) setPasswordPolicy(policy); }).catch(() => undefined);
    return () => { active = false; };
  }, []);

  const requirements = useMemo<PasswordRequirement[]>(() => {
    const next: PasswordRequirement[] = [
      { code: "PASSWORD_TOO_SHORT", label: t("auth.passwordRequirementLength", { count: String(passwordPolicy.requiredLength) }), satisfied: password.length >= passwordPolicy.requiredLength },
    ];
    if (passwordPolicy.requireUppercase) next.push({ code: "PASSWORD_REQUIRES_UPPERCASE", label: t("auth.passwordRequirementUppercase"), satisfied: /[A-Z]/.test(password) });
    if (passwordPolicy.requireLowercase) next.push({ code: "PASSWORD_REQUIRES_LOWERCASE", label: t("auth.passwordRequirementLowercase"), satisfied: /[a-z]/.test(password) });
    if (passwordPolicy.requireDigit) next.push({ code: "PASSWORD_REQUIRES_DIGIT", label: t("auth.passwordRequirementDigit"), satisfied: /\d/.test(password) });
    if (passwordPolicy.requireNonAlphanumeric) next.push({ code: "PASSWORD_REQUIRES_NON_ALPHANUMERIC", label: t("auth.passwordRequirementSymbol"), satisfied: /[^a-zA-Z0-9]/.test(password) });
    if (passwordPolicy.requiredUniqueChars > 1) next.push({ code: "PASSWORD_REQUIRES_UNIQUE_CHARS", label: t("auth.passwordRequirementUnique", { count: String(passwordPolicy.requiredUniqueChars) }), satisfied: new Set(password).size >= passwordPolicy.requiredUniqueChars });
    return next;
  }, [password, passwordPolicy, t]);

  const passwordValid = requirements.every((requirement) => requirement.satisfied);
  const missingPasswordCodes = requirements.filter((requirement) => !requirement.satisfied).map((requirement) => requirement.code);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    setPasswordErrors([]);
    if (isRegister && !passwordValid) { setPasswordErrors(missingPasswordCodes); return; }
    if (isRegister && password !== confirmPassword) { setError(t("auth.passwordMismatch")); return; }
    setSubmitting(true);
    try {
      if (isRegister) await register({ displayName, email, password, preferredLanguage: locale });
      else await signIn({ email, password });
    } catch (caught) {
      if (caught instanceof ApiError && caught.fields?.password) setPasswordErrors(caught.fields.password);
      else setError(t(getAuthErrorTranslationKey(caught)));
    } finally { setSubmitting(false); }
  }

  function passwordErrorLabel(code: string) {
    const labels: Record<string, string> = {
      PASSWORD_TOO_SHORT: t("auth.passwordRequirementLength", { count: String(passwordPolicy.requiredLength) }),
      PASSWORD_REQUIRES_UPPERCASE: t("auth.passwordRequirementUppercase"),
      PASSWORD_REQUIRES_LOWERCASE: t("auth.passwordRequirementLowercase"),
      PASSWORD_REQUIRES_DIGIT: t("auth.passwordRequirementDigit"),
      PASSWORD_REQUIRES_NON_ALPHANUMERIC: t("auth.passwordRequirementSymbol"),
      PASSWORD_REQUIRES_UNIQUE_CHARS: t("auth.passwordRequirementUnique", { count: String(passwordPolicy.requiredUniqueChars) }),
    };
    return labels[code] ?? t("auth.passwordRequirementsInvalid");
  }

  return (
    <div className="auth-page">
      <div className="auth-backdrop"><span /><span /><span /></div>
      <div className="auth-panel">
        <div className="auth-header"><BrandMark /><label className="auth-language"><Languages size={14} /><select value={locale} onChange={(event) => setLocale(event.target.value as typeof locale)} dir="ltr">{locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}</select></label></div>
        <div className="auth-intro"><p className="section-eyebrow">{t("auth.eyebrow")}</p><h1>{isRegister ? t("auth.registerTitle") : t("auth.loginTitle")}</h1><p>{isRegister ? t("auth.registerSubtitle") : t("auth.loginSubtitle")}</p></div>
        <form className="auth-form" onSubmit={submit} dir={localeDirection(locale)}>
          {isRegister && <label><span>{t("auth.displayName")}</span><div className="input-shell"><UserRound size={17} aria-hidden="true" /><input required minLength={2} maxLength={120} value={displayName} onChange={(event) => setDisplayName(event.target.value)} autoComplete="name" dir="auto" /></div></label>}
          <label><span>{t("auth.email")}</span><div className="input-shell"><Mail size={17} aria-hidden="true" /><input required type="email" value={email} onChange={(event) => setEmail(event.target.value)} autoComplete="email" dir="ltr" /></div></label>
          <label className={passwordErrors.length > 0 ? "has-field-error" : ""}><span>{t("auth.password")}</span><div className="input-shell"><LockKeyhole size={17} aria-hidden="true" /><input required minLength={isRegister ? 1 : undefined} type={showPassword ? "text" : "password"} value={password} onChange={(event) => { setPassword(event.target.value); setPasswordErrors([]); }} autoComplete={isRegister ? "new-password" : "current-password"} aria-describedby={isRegister ? "password-requirements" : undefined} dir="ltr" /><button type="button" className="password-toggle" onClick={() => setShowPassword((value) => !value)} aria-label={t("auth.showPassword")}>{showPassword ? <EyeOff size={16} aria-hidden="true" /> : <Eye size={16} aria-hidden="true" />}</button></div>{isRegister && <div className="password-requirements" id="password-requirements" aria-live="polite"><strong>{t("auth.passwordRequirementsTitle")}</strong>{requirements.map((requirement) => <span className={requirement.satisfied ? "is-satisfied" : "is-missing"} key={requirement.code}>{requirement.satisfied ? <Check size={13} aria-hidden="true" /> : <CircleAlert size={13} aria-hidden="true" />}{requirement.label}</span>)}</div>}{passwordErrors.length > 0 && <div className="field-errors" role="alert">{passwordErrors.map((code) => <span key={code}><CircleAlert size={13} aria-hidden="true" />{passwordErrorLabel(code)}</span>)}</div>}</label>
          {isRegister && <label><span>{t("auth.confirmPassword")}</span><div className="input-shell"><LockKeyhole size={17} aria-hidden="true" /><input required type={showPassword ? "text" : "password"} value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} autoComplete="new-password" dir="ltr" /></div></label>}
          {error && <div className="form-error" role="alert">{error}</div>}
          <button className="auth-submit" disabled={submitting || (isRegister && !passwordValid)}>{submitting ? t("auth.working") : isRegister ? t("auth.createAccount") : t("auth.signIn")} {localeDirection(locale) === "ltr" ? <ArrowRight size={17} aria-hidden="true" /> : <ArrowLeft size={17} aria-hidden="true" />}</button>
        </form>
        <p className="auth-switch">{isRegister ? t("auth.haveAccount") : t("auth.noAccount")} <Link href={isRegister ? "/login" : "/register"}>{isRegister ? t("auth.signIn") : t("auth.createAccount")}</Link></p>
      </div>
    </div>
  );
}
