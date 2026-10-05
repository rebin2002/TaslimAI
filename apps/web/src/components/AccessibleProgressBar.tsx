import type { CSSProperties } from "react";

type AccessibleProgressBarProps = Readonly<{
  className: string;
  label: string;
  value: number | null | undefined;
}>;

export function clampAccessibleProgress(value: number | null | undefined) {
  if (!Number.isFinite(value)) return 0;
  return Math.min(100, Math.max(0, value as number));
}

export function AccessibleProgressBar({ className, label, value }: AccessibleProgressBarProps) {
  const progress = clampAccessibleProgress(value);
  const fillStyle: CSSProperties = { width: `${progress}%` };

  return (
    <div
      className={className}
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={progress}
      aria-valuetext={`${progress}%`}
    >
      <span style={fillStyle} />
    </div>
  );
}
