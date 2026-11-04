namespace Taslim.Api.Domain;

public static class PasswordResetDeliveryStates
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Delivered = "Delivered";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// Durable delivery state for at most one password-reset email per user per cooldown.
/// The Identity reset token is generated only by the worker and is never persisted.
/// </summary>
public sealed class PasswordResetEmailOutboxEntry
{
    public Guid UserId { get; set; }
    public DateTime LastRequestedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public string Status { get; set; } = PasswordResetDeliveryStates.Pending;
    public int AttemptCount { get; set; }
    public string? LastFailureCode { get; set; }
}
