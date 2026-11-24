/** Test-only `useLocale` stand-in: the harness runs the English pack only. */
import type { ReactNode } from "react";

export function useLocale(): { locale: "en" } {
  return { locale: "en" };
}

export function LocaleProviderStub({ children }: Readonly<{ children: ReactNode }>) {
  return <>{children}</>;
}
