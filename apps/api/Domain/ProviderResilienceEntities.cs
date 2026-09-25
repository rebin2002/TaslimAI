namespace Taslim.Api.Domain;

public sealed class ProviderCircuit
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string State { get; set; } = "closed";
    public int ConsecutiveFailures { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? OpenUntil { get; set; }
    public DateTime? ProbeExpiresAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public Guid RowVersion { get; set; } = Guid.NewGuid();
    public DateTime UpdatedAt { get; set; }
}

public sealed class ProviderExecutionFinalization
{
    public Guid Id { get; set; }
    public Guid GenerationJobId { get; set; }
    public Guid JobConcurrencyToken { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string State { get; set; } = "claimed";
    public DateTime ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ClaimExpiresAt { get; set; }

    public GenerationJob GenerationJob { get; set; } = null!;
}
