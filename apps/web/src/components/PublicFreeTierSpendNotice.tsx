export type PublicFreeTierSpendNoticeState = "quota" | "unavailable";

export interface PublicFreeTierSpendNoticeProps {
  state: PublicFreeTierSpendNoticeState;
  retryAfterUtc?: string | null;
}

interface FormattedResetTime {
  iso: string;
  label: string;
}

function formatResetTime(value?: string | null): FormattedResetTime | null {
  if (!value || !/(?:Z|[+-]\d{2}:\d{2})$/i.test(value.trim())) return null;

  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return null;

  const iso = date.toISOString();
  return {
    iso,
    label: `${iso.slice(0, 16).replace("T", " ")} UTC`,
  };
}

/**
 * Standalone copy for server-reported public free-tier quota or safety unavailability.
 * This component is intentionally not mounted by any current route in this change.
 */
export function PublicFreeTierSpendNotice({
  state,
  retryAfterUtc,
}: PublicFreeTierSpendNoticeProps) {
  const isQuota = state === "quota";
  const resetTime = isQuota ? formatResetTime(retryAfterUtc) : null;

  return (
    <div
      role="status"
      aria-live="polite"
      aria-atomic="true"
      data-testid="public-free-tier-spend-notice"
      data-state={state}
      style={{
        border: "1px solid #9ca3af",
        borderRadius: "0.5rem",
        padding: "1rem",
        backgroundColor: isQuota ? "#fffbeb" : "#f3f4f6",
        color: "#111827",
      }}
    >
      <p style={{ margin: "0 0 0.5rem" }}>
        <strong>{isQuota ? "Free AI limit reached" : "Free AI temporarily unavailable"}</strong>
      </p>
      {isQuota ? (
        <p style={{ margin: "0 0 0.5rem" }}>
          Your free-plan request limit has been reached.
          {resetTime ? (
            <>
              {" "}You can try again after <time dateTime={resetTime.iso}>{resetTime.label}</time>.
            </>
          ) : (
            " Please check again later."
          )}
        </p>
      ) : (
        <p style={{ margin: "0 0 0.5rem" }}>
          The server could not safely verify provider pricing or current usage, so this capability is temporarily unavailable. Please try again later.
        </p>
      )}
      <p style={{ margin: 0 }}>
        Free-tier provider budgets are separate from customer credit balances.
      </p>
    </div>
  );
}
