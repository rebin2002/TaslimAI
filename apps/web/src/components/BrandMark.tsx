import Link from "next/link";

export function BrandMark({ compact = false }: Readonly<{ compact?: boolean }>) {
  return (
    <Link href="/" className="brand-mark" aria-label="Taslim.ai home">
      <span className="brand-symbol" aria-hidden="true">
        <span className="brand-symbol-core">T</span>
        <span className="brand-orbit brand-orbit-one" />
        <span className="brand-orbit brand-orbit-two" />
        <span className="brand-node brand-node-one" />
        <span className="brand-node brand-node-two" />
        <span className="brand-node brand-node-three" />
      </span>
      {!compact && <span className="brand-wordmark">Taslim<span>.ai</span></span>}
    </Link>
  );
}
