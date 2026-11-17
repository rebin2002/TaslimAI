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

        var migrationTimeoutSeconds = configuration.GetValue(
            "Database:MigrationTimeoutSeconds",
            DatabaseMigrator.DefaultTimeoutSeconds);
        if (migrationTimeoutSeconds is < DatabaseMigrator.MinimumTimeoutSeconds or > DatabaseMigrator.MaximumTimeoutSeconds)
            throw new InvalidOperationException($"Production Database:MigrationTimeoutSeconds must be between {DatabaseMigrator.MinimumTimeoutSeconds} and {DatabaseMigrator.MaximumTimeoutSeconds} seconds.");

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

        ValidateMovieVideo(configuration);
        ValidateDirectVideo(configuration);
        ValidateVoice(configuration);
        ValidateMovieDialogueVoice(configuration);
        ValidateMusic(configuration);
        if (configuration.GetValue("MovieSoundGeneration:Enabled", false))
            throw new InvalidOperationException("Production MovieSoundGeneration has no approved provider-neutral SFX adapter and must remain disabled.");

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

    private static void ValidateMovieVideo(IConfiguration configuration)
    {
        if (!configuration.GetValue("MovieVideo:Enabled", false)) return;
        var provider = configuration["MovieVideo:ProviderKey"]?.Trim().ToLowerInvariant();
        if (provider is not ("runway" or "manus"))
            throw new InvalidOperationException("Production MovieVideo:ProviderKey must be runway or manus when MovieVideo is enabled.");
        if (provider == "runway")
        {
            RequireHttpsUri(configuration["MovieVideo:ApiBaseUrl"], "MovieVideo:ApiBaseUrl");
            RequireValue(configuration["MovieVideo:Model"], "MovieVideo:Model");
            RequireValue(configuration["MovieVideo:ApiKey"], "MovieVideo:ApiKey");
            return;
        }

        if (!configuration.GetValue("MovieVideo:Manus:Enabled", false))
            throw new InvalidOperationException("Production MovieVideo:Manus:Enabled must be true when the Manus provider is selected.");
        RequireHttpsUri(configuration["MovieVideo:Manus:ApiBaseUrl"], "MovieVideo:Manus:ApiBaseUrl");
        RequireValue(configuration["MovieVideo:Manus:ApiKey"], "MovieVideo:Manus:ApiKey");
        RequireValue(configuration["MovieVideo:Manus:AgentProfile"], "MovieVideo:Manus:AgentProfile");
    }

    private static void ValidateDirectVideo(IConfiguration configuration)
    {
        if (!configuration.GetValue("DirectVideoProviders:Enabled", false)) return;
        RequireValue(configuration["DirectVideoProviders:ProviderKey"], "DirectVideoProviders:ProviderKey");
        RequireValue(configuration["DirectVideoProviders:ModelKey"], "DirectVideoProviders:ModelKey");
        RequireHttpsUri(configuration["DirectVideoProviders:ApiBaseUrl"], "DirectVideoProviders:ApiBaseUrl");
        RequireValue(configuration["DirectVideoProviders:ApiKey"], "DirectVideoProviders:ApiKey");
    }

    private static void ValidateVoice(IConfiguration configuration)
    {
        if (!configuration.GetValue("VoiceGeneration:Enabled", false)) return;
        var provider = configuration["VoiceGeneration:ProviderKey"]?.Trim().ToLowerInvariant();
        RequireValue(configuration["VoiceGeneration:Model"], "VoiceGeneration:Model");
        if (provider == "openai")
        {
            if (!configuration.GetValue("Ai:OpenAI:Enabled", false))
                throw new InvalidOperationException("Ai:OpenAI:Enabled must be true when OpenAI voice is selected.");
            RequireValue(configuration["Ai:OpenAI:ApiKey"], "Ai:OpenAI:ApiKey");
            RequireHttpsUri(configuration["Ai:OpenAI:BaseUrl"], "Ai:OpenAI:BaseUrl");
            return;
        }
        if (provider == "azure-speech")
        {
            if (!configuration.GetValue("VoiceGeneration:AzureSpeech:Enabled", false))
                throw new InvalidOperationException("VoiceGeneration:AzureSpeech:Enabled must be true when Azure Speech is selected.");
            RequireValue(configuration["VoiceGeneration:AzureSpeech:ApiKey"], "VoiceGeneration:AzureSpeech:ApiKey");
            var endpoint = configuration["VoiceGeneration:AzureSpeech:Endpoint"];
            var region = configuration["VoiceGeneration:AzureSpeech:Region"];
            if (string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(region))
                throw new InvalidOperationException("Azure Speech production configuration requires a region or HTTPS endpoint.");
            if (!string.IsNullOrWhiteSpace(endpoint)) RequireHttpsUri(endpoint, "VoiceGeneration:AzureSpeech:Endpoint");
            if (!string.IsNullOrWhiteSpace(region) && (region.Contains('/') || region.Contains(':')))
                throw new InvalidOperationException("VoiceGeneration:AzureSpeech:Region is invalid.");
            return;
        }
        throw new InvalidOperationException("Production VoiceGeneration:ProviderKey must be openai or azure-speech when voice generation is enabled.");
    }

    private static void ValidateMovieDialogueVoice(IConfiguration configuration)
    {
        if (!configuration.GetValue("MovieDialogueVoice:Enabled", false)) return;
        var movieProvider = configuration["MovieDialogueVoice:ProviderKey"]?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(movieProvider) || movieProvider is "disabled" or "unconfigured" or "fake")
            throw new InvalidOperationException("Production MovieDialogueVoice:ProviderKey must select an approved configured voice provider.");
        if (!configuration.GetValue("VoiceGeneration:Enabled", false)
            || !string.Equals(movieProvider, configuration["VoiceGeneration:ProviderKey"]?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MovieDialogueVoice must select the same enabled provider as VoiceGeneration.");
        ValidateVoice(configuration);
    }

    private static void ValidateMusic(IConfiguration configuration)
    {
        if (!configuration.GetValue("MusicGeneration:Enabled", false)) return;
        var provider = configuration["MusicGeneration:ProviderKey"]?.Trim().ToLowerInvariant();
        RequireValue(configuration["MusicGeneration:Model"], "MusicGeneration:Model");
        if (provider == "mubert")
        {
            RequireHttpsUri(configuration["MusicGeneration:MubertApiBaseUrl"], "MusicGeneration:MubertApiBaseUrl");
            RequireValue(configuration["MusicGeneration:MubertCustomerId"], "MusicGeneration:MubertCustomerId");
            RequireValue(configuration["MusicGeneration:MubertAccessToken"], "MusicGeneration:MubertAccessToken");
            return;
        }
        if (provider == "stable-audio")
        {
            RequireHttpsUri(configuration["MusicGeneration:StableAudioApiBaseUrl"], "MusicGeneration:StableAudioApiBaseUrl");
            RequireValue(configuration["MusicGeneration:StableAudioApiKey"], "MusicGeneration:StableAudioApiKey");
            RequireValue(configuration["MusicGeneration:StableAudioModel"], "MusicGeneration:StableAudioModel");
            return;
        }
        throw new InvalidOperationException("Production MusicGeneration:ProviderKey must be mubert or stable-audio when music generation is enabled.");
    }
}
