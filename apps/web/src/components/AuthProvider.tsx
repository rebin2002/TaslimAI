"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { api, type AuthResponse, type LoginInput, type ProfileInput, type RegisterInput, type User } from "@/lib/api";
import { useLocale } from "@/components/LocaleProvider";

type AuthContextValue = {
  user: User | null;
  workspace: AuthResponse["personalWorkspace"] | null;
  loading: boolean;
  signIn: (input: LoginInput) => Promise<void>;
  register: (input: RegisterInput) => Promise<void>;
  signOut: () => Promise<void>;
  updateProfile: (input: ProfileInput) => Promise<void>;
  refresh: () => Promise<void>;
};

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: Readonly<{ children: React.ReactNode }>) {
  const [session, setSession] = useState<AuthResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const { setLocale } = useLocale();
  const router = useRouter();

  const refresh = useCallback(async () => {
    try {
      const next = await api.me();
      setSession(next);
    } catch {
      setSession(null);
    } finally {
      setLoading(false);
    }
  }, []);

  // The refresh synchronizes React state with the server session after mount.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => { if (session?.user.preferredLanguage) setLocale(session.user.preferredLanguage); }, [session?.user.preferredLanguage, setLocale]);

  const signIn = useCallback(async (input: LoginInput) => {
    const next = await api.login(input);
    setSession(next);
    router.push("/projects");
  }, [router]);

  const register = useCallback(async (input: RegisterInput) => {
    const next = await api.register(input);
    setSession(next);
    router.push("/projects");
  }, [router]);

  const signOut = useCallback(async () => {
    await api.logout();
    setSession(null);
    router.push("/");
  }, [router]);

  const updateProfile = useCallback(async (input: ProfileInput) => {
    const next = await api.updateProfile(input);
    setSession(next);
  }, []);

  const value = useMemo(() => ({ user: session?.user ?? null, workspace: session?.personalWorkspace ?? null, loading, signIn, register, signOut, updateProfile, refresh }), [loading, refresh, register, session, signIn, signOut, updateProfile]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used within AuthProvider");
  return value;
}
