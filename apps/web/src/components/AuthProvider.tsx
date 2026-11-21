"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { api, type AuthResponse, type LoginInput, type OnboardingInput, type ProfileInput, type RegisterInput, type User } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";
import { accountLocaleStorageKey, anonymousLocaleStorageKey, readStoredLocale, writeStoredLocale } from "@/lib/localePersistence";
import { accountLanguagePackStorageKey, anonymousLanguagePackStorageKey, readStoredLanguagePack, writeStoredLanguagePack } from "@/lib/languagePackPersistence";
import { defaultLanguagePackRegistry, legacyLocaleForLanguagePack } from "@/lib/languagePackRegistry";

type AuthContextValue = {
  user: User | null;
  workspace: AuthResponse["personalWorkspace"] | null;
  loading: boolean;
  signIn: (input: LoginInput, redirectTo?: string) => Promise<void>;
  register: (input: RegisterInput) => Promise<void>;
  signOut: () => Promise<void>;
  updateProfile: (input: ProfileInput) => Promise<void>;
  completeOnboarding: (input: OnboardingInput) => Promise<void>;
  refresh: () => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

// #992 compatibility: the pack key represents every installed language, while
// the legacy account key must still record explicit EN/AR/Sorani choices.
function saveExplicitAccountLanguagePack(storage: Storage, userId: string, packId: string) {
  writeStoredLanguagePack(storage, accountLanguagePackStorageKey(userId), packId);
  const legacy = legacyLocaleForLanguagePack(packId);
  if (legacy === packId) {
    writeStoredLocale(storage, accountLocaleStorageKey(userId), legacy);
  } else {
    storage.removeItem(accountLocaleStorageKey(userId));
  }
}

export function AuthProvider({ children }: Readonly<{ children: React.ReactNode }>) {
  const [session, setSession] = useState<AuthResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const { getLocaleSource, languagePackId, setLanguagePack } = useLocale();
  const router = useRouter();
  const sessionLanguagePackInitialized = useRef<string | null>(null);
  const authRequestGeneration = useRef(0);

  const refresh = useCallback(async () => {
    const request = ++authRequestGeneration.current;
    try {
      const next = await api.me();
      if (request !== authRequestGeneration.current) return;
      setSession(next);
    } catch {
      if (request !== authRequestGeneration.current) return;
      setSession(null);
      sessionLanguagePackInitialized.current = null;
    } finally {
      if (request === authRequestGeneration.current) setLoading(false);
    }
  }, []);

  // The refresh synchronizes React state with the server session after mount.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => {
    const user = session?.user;
    if (!user) {
      sessionLanguagePackInitialized.current = null;
      return;
    }
    if (sessionLanguagePackInitialized.current !== user.id) {
      sessionLanguagePackInitialized.current = user.id;
      // Browser storage may be unavailable in privacy-restricted contexts.
      // Never let a denied localStorage getter abort authenticated session adoption.
      let savedLanguagePack: string | null = null;
      let deviceLocaleSelection: string | null = null;
      let storageReadable = true;
      const localeSource = getLocaleSource();
      try {
        const storage = window.localStorage;
        savedLanguagePack = readStoredLanguagePack(storage, accountLanguagePackStorageKey(user.id))
          ?? readStoredLocale(storage, accountLocaleStorageKey(user.id));
        if (localeSource === "anonymous-storage") deviceLocaleSelection = readStoredLocale(storage);
      } catch {
        storageReadable = false;
        // The server profile is the fallback if the browser denies storage.
      }
      const preferredLanguagePack = defaultLanguagePackRegistry.has(user.preferredLanguage) ? user.preferredLanguage : "en";
      // `LocaleProvider` ranks the legacy `taslim-locale` device key above the pack
      // bootstrap key because every lane persists an explicit in-browser selection
      // through it - the Arabic and Sorani right-to-left journeys included. While the
      // account holds no saved choice of its own, that device selection outranks the
      // profile default: adopting the account value instead would flip those readers
      // back to English, and copying it into the account key would strand them on the
      // first language they picked, so a later switch through the same key is ignored.
      const deviceSelectionIsSovereign = storageReadable && Boolean(deviceLocaleSelection) && !savedLanguagePack;
      if (!deviceSelectionIsSovereign && localeSource !== "user"
        && (!storageReadable || localeSource !== "anonymous-storage" || savedLanguagePack)) {
        setLanguagePack(savedLanguagePack ?? preferredLanguagePack, { persist: false, source: "account" });
      }
    }
  }, [getLocaleSource, session?.user, setLanguagePack]);
  useEffect(() => {
    const userId = session?.user.id;
    // The first-login picker is a preview until the server accepts onboarding.
    // Persisting it here would override the saved account profile after reload
    // even when completion fails or the user abandons the flow.
    if (!userId || sessionLanguagePackInitialized.current !== userId || session?.user.onboardingCompletedAt === null) return;
    try {
      // Only an explicit selector choice is local account preference.
      // Server-profile adoption or anonymous bootstrap must never overwrite it.
      if (getLocaleSource() === "user") {
        saveExplicitAccountLanguagePack(window.localStorage, userId, languagePackId);
      }
      // Keep the bootstrap while it is the reader's own authority - nothing is saved on
      // the account to outrank it and it is the value being displayed. Clearing it there
      // would drop the language on the next document load and stop the same contract
      // from carrying a later switch to another language.
      const savedAccountChoice = readStoredLanguagePack(window.localStorage, accountLanguagePackStorageKey(userId))
        ?? readStoredLocale(window.localStorage, accountLocaleStorageKey(userId));
      const deviceBootstrap = readStoredLocale(window.localStorage) ?? readStoredLanguagePack(window.localStorage, anonymousLanguagePackStorageKey);
      const deviceIsAuthority = getLocaleSource() === "anonymous-storage" && deviceBootstrap === languagePackId && !savedAccountChoice;
      if (deviceIsAuthority) {
        // The unsaved device choice survives reload via the pack key, while
        // consuming the legacy device key as the #992 account flow requires.
        // A later legacy-key selection still wins on the next document load.
        writeStoredLanguagePack(window.localStorage, anonymousLanguagePackStorageKey, languagePackId);
        window.localStorage.removeItem(anonymousLocaleStorageKey);
      } else {
        window.localStorage.removeItem(anonymousLanguagePackStorageKey);
        window.localStorage.removeItem(anonymousLocaleStorageKey);
      }
    } catch {
      // Storage can be unavailable; the selected pack remains active in memory.
    }
  }, [getLocaleSource, languagePackId, session?.user.id, session?.user.onboardingCompletedAt]);

  const signIn = useCallback(async (input: LoginInput, redirectTo?: string) => {
    const request = ++authRequestGeneration.current;
    const next = await api.login(input);
    if (request !== authRequestGeneration.current) return;
    setSession(next);
    if (redirectTo) router.replace(redirectTo);
    else router.push("/projects");
  }, [router]);

  const register = useCallback(async (input: RegisterInput) => {
    const request = ++authRequestGeneration.current;
    const next = await api.register(input);
    if (request !== authRequestGeneration.current) return;
    setSession(next);
    router.push("/projects");
  }, [router]);

  const signOut = useCallback(async () => {
    const request = ++authRequestGeneration.current;
    await api.logout();
    if (request !== authRequestGeneration.current) return;
    setSession(null);
    sessionLanguagePackInitialized.current = null;
    setLanguagePack("en", { persist: false, source: "default" });
    try {
      window.localStorage.removeItem(anonymousLanguagePackStorageKey);
      window.localStorage.removeItem(anonymousLocaleStorageKey);
    } catch {
      // Storage can be unavailable; signing out and resetting in-memory state still succeeds.
    }
    router.push("/");
  }, [router, setLanguagePack]);

  const updateProfile = useCallback(async (input: ProfileInput) => {
    const next = await api.updateProfile(input);
    setSession(next);
    // The profile endpoint persists the interface language. Apply the same
    // choice immediately so Account does not require a reload to switch copy,
    // document language, or direction.
    if (defaultLanguagePackRegistry.has(input.preferredLanguage)) {
      setLanguagePack(input.preferredLanguage, { persist: false, source: "account" });
      // A successful profile save is an explicit account preference.
      try {
        saveExplicitAccountLanguagePack(window.localStorage, next.user.id, input.preferredLanguage);
      } catch {
        // The saved server preference and in-memory choice remain authoritative.
      }
    }
  }, [setLanguagePack]);

  const completeOnboarding = useCallback(async (input: OnboardingInput) => {
    const next = await api.completeOnboarding(input);
    // The picker only marks the locale source as a user choice when the reader actually
    // changed it, so read that before adoption rewrites the source.
    const selectionWasMade = getLocaleSource() === "user";
    setSession(next);
    if (defaultLanguagePackRegistry.has(input.preferredLanguage)) {
      setLanguagePack(input.preferredLanguage, { persist: false, source: "account" });
      // First-login selection becomes durable only after onboarding succeeds, and only
      // when it really was a selection. Completing the dialog untouched accepts the
      // inherited default, and recording that as an account choice would let it
      // outrank the device language every right-to-left lane selects through
      // `taslim-locale`.
      if (selectionWasMade) {
        try {
          saveExplicitAccountLanguagePack(window.localStorage, next.user.id, input.preferredLanguage);
        } catch {
          // Keep the accepted server value even without browser storage.
        }
      }
    }
  }, [getLocaleSource, setLanguagePack]);

  const value = useMemo(() => ({ user: session?.user ?? null, workspace: session?.personalWorkspace ?? null, loading, signIn, register, signOut, updateProfile, completeOnboarding, refresh }), [completeOnboarding, loading, refresh, register, session, signIn, signOut, updateProfile]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used within AuthProvider");
  return value;
}
