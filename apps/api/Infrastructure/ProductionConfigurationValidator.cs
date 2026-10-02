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

        // Autopilot safety invariants. These can only be relaxed through an
        // explicit, reviewed human decision; production never starts with paid
        // capabilities enabled, and an enabled controller always requires
        // signed events plus a server-side signing secret.
        // Staged activation is supported: Stage 1 runs the controller enabled but
        // still simulating (Autopilot:Enabled=true with Autopilot:DryRun=true),
        // which is the supervised observation posture. Live execution stays a
        // separate, explicit human decision that requires DryRun=false; the
        // shipped defaults keep the controller disabled and simulating.
        if (configuration.GetValue("Autopilot:ChargingEnabled", false))
            throw new InvalidOperationException("Autopilot charging must remain disabled until a human decision approves it.");
        if (configuration.GetValue("Autopilot:PaidProvidersEnabled", false))
            throw new InvalidOperationException("Autopilot paid providers must remain disabled until a human decision approves them.");

        if (configuration.GetValue("Autopilot:Enabled", false))
        {
            if (!configuration.GetValue("Autopilot:RequireSignedEvents", true))
                throw new InvalidOperationException("Autopilot:RequireSignedEvents must stay enabled in production.");
            var signingSecretVariable = configuration["Autopilot:SigningSecretEnvironmentVariable"] ?? "AUTOPILOT_WEBHOOK_SECRET";
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(signingSecretVariable)))
                throw new InvalidOperationException($"Autopilot requires the server-side signing secret environment variable {signingSecretVariable}.");
        }

        // Live follow-on wave launch is a separate, explicit capability. It is never
        // enabled implicitly: it must be requested and it must have both a bridge
        // endpoint and a server-side bridge credential available. The hard bounds are
        // re-asserted here so an unsafe bound can never reach production.
        if (configuration.GetValue("Autopilot:AllowNextWaveLaunch", false)
            && !configuration.GetValue("Autopilot:DryRun", true))
        {
            RequireHttpsUri(configuration["Autopilot:BridgeBaseUrl"], "Autopilot:BridgeBaseUrl");
            var bridgeTokenVariable = configuration["Autopilot:BridgeTokenEnvironmentVariable"] ?? "TASLIM_BRIDGE_TOKEN";
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(bridgeTokenVariable)))
                throw new InvalidOperationException($"Autopilot live next-wave launch requires the server-side bridge credential environment variable {bridgeTokenVariable}.");
            if (configuration.GetValue("Autopilot:MaxTasksPerWave", 20) > 20)
                throw new InvalidOperationException("Autopilot:MaxTasksPerWave must not exceed 20.");
            if (configuration.GetValue("Autopilot:MaxLaunchAttempts", 3) > 3)
                throw new InvalidOperationException("Autopilot:MaxLaunchAttempts must not exceed 3.");
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
