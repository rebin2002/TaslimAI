import type { Metadata } from "next";
import { PasswordRecoveryForm } from "@/components/PasswordRecoveryForm";

export const metadata: Metadata = {
  title: "Reset your Taslim.ai password",
  robots: { index: false, follow: false },
  referrer: "no-referrer",
};

export default function ForgotPasswordPage() {
  return <PasswordRecoveryForm mode="request" />;
}
