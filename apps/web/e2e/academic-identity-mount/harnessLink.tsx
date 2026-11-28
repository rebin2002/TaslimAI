/** Test-only `next/link` stand-in so the harness needs no Next runtime. */
import type { ReactNode } from "react";

export default function Link({
  href,
  className,
  children,
}: Readonly<{ href: string; className?: string; children?: ReactNode }>) {
  return (
    <a href={href} className={className}>
      {children}
    </a>
  );
}
