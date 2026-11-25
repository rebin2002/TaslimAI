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
            SslMode = ResolveSslMode(uri),
        };

        if (GetQueryParameter(uri, "sslrootcert") is { Length: > 0 } rootCertificate)
            builder.RootCertificate = rootCertificate;

        return builder.ConnectionString;
    }

    private static SslMode ResolveSslMode(Uri uri) => GetQueryParameter(uri, "sslmode")?.ToLowerInvariant() switch
    {
        "verify-ca" => SslMode.VerifyCA,
        "verify-full" => SslMode.VerifyFull,
        // Keep the existing TLS requirement; URI options cannot weaken it.
        _ => SslMode.Require,
    };

    private static string? GetQueryParameter(Uri uri, string name)
    {
        foreach (var component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = component.IndexOf('=');
            var rawName = separator < 0 ? component : component[..separator];
            var decodedName = Uri.UnescapeDataString(rawName.Replace('+', ' '));
            if (!string.Equals(decodedName, name, StringComparison.OrdinalIgnoreCase))
                continue;

            var rawValue = separator < 0 ? string.Empty : component[(separator + 1)..];
            return Uri.UnescapeDataString(rawValue.Replace('+', ' '));
        }

        return null;
    }
}
