using Npgsql;

namespace Taslim.Api.Infrastructure;

public static class DatabaseConnectionString
{
    public static string Resolve(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("Postgres")
            ?? configuration["ConnectionStrings__Postgres"]
            ?? configuration["DATABASE_URL"]
            ?? "Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me";
        return Normalize(raw);
    }

    public static string Normalize(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "postgresql" && uri.Scheme != "postgres"))
            return raw;

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(uri.UserInfo.Split(':').FirstOrDefault() ?? string.Empty),
            Password = Uri.UnescapeDataString(uri.UserInfo.Contains(':') ? uri.UserInfo[(uri.UserInfo.IndexOf(':') + 1)..] : string.Empty),
            SslMode = SslMode.Require,
        };
        return builder.ConnectionString;
    }
}
