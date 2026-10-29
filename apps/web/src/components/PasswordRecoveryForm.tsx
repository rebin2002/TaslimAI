"use client";

import Link from "next/link";
import { type FormEvent, useEffect, useMemo, useState } from "react";
import { ArrowLeft, ArrowRight, Check, CircleAlert, LockKeyhole, Mail, Languages } from "lucide-react";
import { BrandMark } from "@/components/BrandMark";
import { useLocale, localeNames, locales } from "@/components/LocaleProvider";
import { api, ApiError, type PasswordPolicy } from "@/lib/api";
import { localeDirection } from "@/lib/i18n";

type RecoveryMode = "request" | "reset";
type PasswordRequirement = { code: string; label: string; satisfied: boolean };

const initialPasswordPolicy: PasswordPolicy = {
  requiredLength: 10,
  requireUppercase: true,
  requireLowercase: true,
  requireDigit: true,
  requireNonAlphanumeric: true,
  requiredUniqueChars: 1,
};

export function PasswordRecoveryForm({ mode }: Readonly<{ mode: RecoveryMode }>) {
  const { t, locale, setLocale } = useLocale();
  const [email, setEmail] = useState("");
  const [resetToken, setResetToken] = useState("");
  const [linkState, setLinkState] = useState<"loading" | "ready" | "invalid">(mode === "reset" ? "loading" : "ready");
  const [requestSent, setRequestSent] = useState(false);
  const [resetComplete, setResetComplete] = useState(false);
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [passwordPolicy, setPasswordPolicy] = useState<PasswordPolicy>(initialPasswordPolicy);
  const [passwordErrors, setPasswordErrors] = useState<string[]>([]);
  const [error, setError] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [recoveryAvailable, setRecoveryAvailable] = useState(false);
  const [availabilityResolved, setAvailabilityResolved] = useState(false);
  const isReset = mode === "reset";

  useEffect(() => {
    let active = true;
    void api.passwordRecoveryAvailability()
      .then(({ available }) => { if (active) setRecoveryAvailable(available === true); })
      .catch(() => { if (active) setRecoveryAvailable(false); })
      .finally(() => { if (active) setAvailabilityResolved(true); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (!isReset) return;
    let active = true;
    const fragment = new URLSearchParams(window.location.hash.slice(1));
    const linkEmail = fragment.get("email") ?? "";
    const linkToken = fragment.get("token") ?? "";
    const linkLanguage = fragment.get("lang");

    // Remove the fragment (and any accidental query string) before rendering or
    // making API calls. URL fragments are not sent to the server or in Referer
    // headers, and the token is never displayed or written to logs/storage.
    window.history.replaceState(window.history.state, "", window.location.pathname);
    void Promise.resolve().then(() => {
      if (!active) return;
      if (linkLanguage === "en" || linkLanguage === "ar" || linkLanguage === "ku")
        setLocale(linkLanguage, { source: "account" });
      setEmail(linkEmail);
      setResetToken(linkToken);
      setLinkState(linkEmail.length <= 256 && linkToken.length > 0 && linkToken.length <= 4096 ? "ready" : "invalid");
    });
    void api.passwordPolicy().then((policy) => { if (active) setPasswordPolicy(policy); }).catch(() => undefined);
    return () => { active = false; };
  }, [isReset, setLocale]);

  const requirements = useMemo<PasswordRequirement[]>(() => {
    const result: PasswordRequirement[] = [
      { code: "PASSWORD_TOO_SHORT", label: t("auth.passwordRequirementLength", { count: String(passwordPolicy.requiredLength) }), satisfied: newPassword.length >= passwordPolicy.requiredLength },
    ];
    if (passwordPolicy.requireUppercase) result.push({ code: "PASSWORD_REQUIRES_UPPERCASE", label: t("auth.passwordRequirementUppercase"), satisfied: /[A-Z]/.test(newPassword) });
    if (passwordPolicy.requireLowercase) result.push({ code: "PASSWORD_REQUIRES_LOWERCASE", label: t("auth.passwordRequirementLowercase"), satisfied: /[a-z]/.test(newPassword) });
    if (passwordPolicy.requireDigit) result.push({ code: "PASSWORD_REQUIRES_DIGIT", label: t("auth.passwordRequirementDigit"), satisfied: /\d/.test(newPassword) });
    if (passwordPolicy.requireNonAlphanumeric) result.push({ code: "PASSWORD_REQUIRES_NON_ALPHANUMERIC", label: t("auth.passwordRequirementSymbol"), satisfied: /[^a-zA-Z0-9]/.test(newPassword) });
    if (passwordPolicy.requiredUniqueChars > 1) result.push({ code: "PASSWORD_REQUIRES_UNIQUE_CHARS", label: t("auth.passwordRequirementUnique", { count: String(passwordPolicy.requiredUniqueChars) }), satisfied: new Set(newPassword).size >= passwordPolicy.requiredUniqueChars });
    return result;
  }, [newPassword, passwordPolicy, t]);

  const missingPasswordCodes = requirements.filter((requirement) => !requirement.satisfied).map((requirement) => requirement.code);
  const passwordValid = missingPasswordCodes.length === 0;

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

  async function submitRequest(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    setSubmitting(true);
    try {
      await api.requestPasswordReset({ email: email.trim() });
      setEmail("");
      setRequestSent(true);
    } catch (caught) {
      if (caught instanceof ApiError && caught.status === 503) {
        setRecoveryAvailable(false);
        setError(t("auth.recoveryUnavailable"));
      } else {
        setError(t("auth.genericError"));
      }
    } finally {
      setSubmitting(false);
    }
  }

  async function submitReset(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    setPasswordErrors([]);
    if (newPassword !== confirmPassword) {
      setError(t("auth.passwordMismatch"));
      return;
    }
    if (!passwordValid) {
      setPasswordErrors(missingPasswordCodes);
      return;
    }

    setSubmitting(true);
    try {
      await api.resetPassword({ email, token: resetToken, newPassword });
      setResetToken("");
      setNewPassword("");
      setConfirmPassword("");
      setResetComplete(true);
    } catch (caught) {
      if (caught instanceof ApiError && caught.fields?.newPassword?.length) {
        setPasswordErrors(caught.fields.newPassword);
      } else if (caught instanceof ApiError && caught.code === "PASSWORD_RESET_INVALID") {
        setLinkState("invalid");
        setError(t("auth.resetInvalid"));
      } else if (caught instanceof ApiError && caught.status === 503) {
        setRecoveryAvailable(false);
        setError(t("auth.recoveryUnavailable"));
      } else {
        setError(t("auth.genericError"));
      }
    } finally {
      setSubmitting(false);
    }
  }

  const direction = localeDirection(locale);
  return (
    <div className="auth-page">
      <div className="auth-backdrop"><span /><span /><span /></div>
      <section className="auth-panel" aria-labelledby="recovery-title">
        <div className="auth-header">
          <BrandMark />
          <label className="auth-language"><Languages size={14} aria-hidden="true" /><select aria-label={t("navigation.language")} value={locale} onChange={(event) => setLocale(event.target.value as typeof locale)} dir="ltr">{locales.map((item) => <option key={item} value={item}>{localeNames[item]}</option>)}</select></label>
        </div>
        <div className="auth-intro">
          <p className="section-eyebrow">{t(isReset ? "auth.eyebrow" : "auth.forgotEyebrow")}</p>
          <h1 id="recovery-title">{t(isReset ? "auth.resetTitle" : "auth.forgotTitle")}</h1>
          <p>{t(isReset ? "auth.resetSubtitle" : "auth.forgotSubtitle")}</p>
        </div>

        {!availabilityResolved && <p className="auth-recovery-message" role="status">{t("auth.working")}</p>}
        {availabilityResolved && !recoveryAvailable && <div className="auth-recovery-message is-error" role="status" aria-live="polite">{t("auth.recoveryUnavailable")}</div>}

        {availabilityResolved && recoveryAvailable && !isReset && !requestSent && <form className="auth-form" onSubmit={submitRequest} dir={direction}>
          <label><span>{t("auth.email")}</span><div className="input-shell"><Mail size={17} aria-hidden="true" /><input required type="email" maxLength={256} value={email} onChange={(event) => setEmail(event.target.value)} autoComplete="email" autoCapitalize="none" spellCheck={false} dir="ltr" /></div></label>
          {error && <div className="form-error" role="alert">{error}</div>}
          <button className="auth-submit" disabled={submitting}>{submitting ? t("auth.passwordResetWorking") : t("auth.sendResetLink")} {direction === "ltr" ? <ArrowRight size={17} aria-hidden="true" /> : <ArrowLeft size={17} aria-hidden="true" />}</button>
        </form>}

        {availabilityResolved && recoveryAvailable && !isReset && requestSent && <div className="auth-recovery-message is-success" role="status" aria-live="polite">{t("auth.resetRequestSent")}</div>}

        {availabilityResolved && recoveryAvailable && isReset && linkState === "loading" && <p className="auth-recovery-message" role="status">{t("auth.working")}</p>}
        {availabilityResolved && recoveryAvailable && isReset && linkState === "invalid" && <div className="auth-recovery-message is-error" role="status" aria-live="polite">{error || t("auth.resetLinkMissing")}</div>}
        {availabilityResolved && recoveryAvailable && isReset && linkState === "ready" && !resetComplete && <form className="auth-form" onSubmit={submitReset} dir={direction}>
          <label className={passwordErrors.length > 0 ? "has-field-error" : ""}><span>{t("auth.newPassword")}</span><div className="input-shell"><LockKeyhole size={17} aria-hidden="true" /><input required type="password" maxLength={128} value={newPassword} onChange={(event) => { setNewPassword(event.target.value); setPasswordErrors([]); }} autoComplete="new-password" aria-describedby="password-requirements" dir="ltr" /></div>
            <div className="password-requirements" id="password-requirements" aria-live="polite"><strong>{t("auth.passwordRequirementsTitle")}</strong>{requirements.map((requirement) => <span className={requirement.satisfied ? "is-satisfied" : "is-missing"} key={requirement.code}>{requirement.satisfied ? <Check size={13} aria-hidden="true" /> : <CircleAlert size={13} aria-hidden="true" />}{requirement.label}</span>)}</div>
            {passwordErrors.length > 0 && <div className="field-errors" role="alert">{passwordErrors.map((code) => <span key={code}><CircleAlert size={13} aria-hidden="true" />{passwordErrorLabel(code)}</span>)}</div>}
          </label>
          <label><span>{t("auth.confirmPassword")}</span><div className="input-shell"><LockKeyhole size={17} aria-hidden="true" /><input required type="password" maxLength={128} value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} autoComplete="new-password" dir="ltr" /></div></label>
          {error && <div className="form-error" role="alert">{error}</div>}
          <button className="auth-submit" disabled={submitting}>{submitting ? t("auth.passwordResetWorking") : t("auth.resetPassword")} {direction === "ltr" ? <ArrowRight size={17} aria-hidden="true" /> : <ArrowLeft size={17} aria-hidden="true" />}</button>
        </form>}

        {availabilityResolved && recoveryAvailable && isReset && resetComplete && <div className="auth-recovery-message is-success" role="status" aria-live="polite">{t("auth.resetSuccess")}</div>}
        <p className="auth-switch">{recoveryAvailable && isReset && linkState === "invalid" && <><Link href="/forgot-password">{t("auth.forgotPassword")}</Link> · </>}<Link href="/login">{t("auth.backToLogin")}</Link>{recoveryAvailable && !isReset && !requestSent && <> · <Link href="/register">{t("auth.createAccount")}</Link></>}</p>
      </section>
    </div>
  );
}
