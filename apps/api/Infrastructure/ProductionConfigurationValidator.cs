using System.Net;

namespace Taslim.Api.Infrastructure;

public static class ProductionConfigurationValidator
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsProduction()) return;

        var connectionString = configuration.GetConnectionString("Postgres")
            ?? configuration["ConnectionStrings__Postgres"]
            ?? configuration["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("change-me", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production database configuration is missing or uses a placeholder value.");

        var origins = configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0)
            throw new InvalidOperationException("Production AllowedOrigins must contain at least one HTTPS origin.");
        foreach (var origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.UserInfo.Length > 0
                || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address))
                throw new InvalidOperationException("Production AllowedOrigins must contain only trusted HTTPS origins.");
        }

        var storageProvider = configuration["Files:StorageProvider"] ?? "";
        if (string.Equals(storageProvider, "S3Compatible", StringComparison.OrdinalIgnoreCase))
        {
            RequireHttpsUri(configuration["Files:S3Endpoint"], "Files:S3Endpoint");
            RequireValue(configuration["Files:S3Region"], "Files:S3Region");
            RequireValue(configuration["Files:S3Bucket"], "Files:S3Bucket");
            RequireValue(configuration["Files:S3AccessKey"], "Files:S3AccessKey");
            RequireValue(configuration["Files:S3SecretKey"], "Files:S3SecretKey");
        }
        else if (!string.Equals(storageProvider, "S3Compatible", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Production Files:StorageProvider must be S3Compatible for persistent storage.");
        }

        if (configuration.GetValue("Billing:CustomerChargingEnabled", false))
            throw new InvalidOperationException("Customer charging must remain disabled until payment production readiness is approved.");

        if (configuration.GetValue("Ai:OpenAI:Enabled", false))
        {
            RequireValue(configuration["Ai:OpenAI:ApiKey"], "Ai:OpenAI:ApiKey");
            RequireHttpsUri(configuration["Ai:OpenAI:BaseUrl"], "Ai:OpenAI:BaseUrl");
        }
    }

    private static void RequireValue(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Production configuration {key} is required.");
    }

    private static void RequireHttpsUri(string? value, string key)
    {
        RequireValue(value, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Production configuration {key} must be an HTTPS URL.");
    }
}
