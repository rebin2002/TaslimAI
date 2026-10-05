namespace Taslim.Api.Infrastructure;

/// <summary>
/// Normalizes offset pagination at the API boundary so hostile or accidental
/// page values cannot overflow SQL offsets or trigger unbounded deep scans.
/// </summary>
public static class ApiPagination
{
    public const int MaxPage = 10_000;
    public const int MaxPageSize = 100;

    public static int NormalizePage(int page) => Math.Clamp(page, 1, MaxPage);

    public static int NormalizePageSize(int pageSize) => Math.Clamp(pageSize, 1, MaxPageSize);

    public static int GetOffset(int page, int pageSize)
    {
        var normalizedPage = NormalizePage(page);
        var normalizedPageSize = NormalizePageSize(pageSize);
        return checked((normalizedPage - 1) * normalizedPageSize);
    }
}
