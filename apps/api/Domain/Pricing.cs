using System.Text.Json;

namespace Taslim.Api.Domain;

public sealed record UsagePricingSnapshot(
    string Provider,
    string Model,
    string Version,
    DateTime EffectiveAtUtc,
    string Unit,
    string Source,
    IReadOnlyDictionary<string, decimal> Rates)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}

public static class UsageCurrencies
{
    public const string Usd = "USD";
}
