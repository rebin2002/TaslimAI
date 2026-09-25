using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Taslim.Api.Ai;
using Taslim.Api.Activity;
using Taslim.Api.Assets;
using Taslim.Api.Authorization;
using Taslim.Api.Billing;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Presentations;
using Taslim.Api.Research;
using Taslim.Api.Social;
using Taslim.Api.Operations;
using Taslim.Api.Infrastructure;
using Taslim.Api.Images;
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Payments;
using Taslim.Api.Notifications;
using Taslim.Api.Voice;
using FileSettings = Taslim.Api.Files.FileOptions;

var builder = WebApplication.CreateBuilder(args);
var isProduction = builder.Environment.IsProduction();

builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add(new ProducesAttribute("application/json"));
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
            new BadRequestObjectResult(new { error = new { code = "VALIDATION_ERROR", message = "Please check the highlighted fields." } });
    });
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<HealthOptions>(builder.Configuration.GetSection("Health"));
builder.Services.AddScoped<OperationalHealthService>();
builder.Services.AddOptions<FileSettings>().Bind(builder.Configuration.GetSection("Files"));
builder.Services.Configure<FormOptions>(options =>
{
    var maxFileSize = builder.Configuration.GetValue<long>("Files:MaxFileSizeBytes", 25 * 1024 * 1024);
    options.MultipartBodyLengthLimit = maxFileSize + (1024 * 1024);
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Railway terminates TLS at its edge and forwards the original scheme.
    // Only consume the scheme, and only one proxy hop, because the API does
    // not need forwarded client IP or host data for authentication/CSRF.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    var configuredProxyIps = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
    foreach (var value in configuredProxyIps)
    {
        if (System.Net.IPAddress.TryParse(value, out var address)) options.KnownProxies.Add(address);
    }

    if (isProduction && configuredProxyIps.Length == 0)
    {
        // Railway's public ingress source IPs are not a stable application
        // setting. The service is reachable through Railway's ingress, so
        // trust exactly one upstream hop while allowing an operator to add
        // explicit KnownProxies if the hosting topology becomes stricter.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    }
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = DatabaseConnectionString.Resolve(builder.Configuration);
builder.Services.AddDbContext<TaslimDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDataProtection()
    .SetApplicationName("Taslim.Api")
    .PersistKeysToDbContext<TaslimDbContext>();

builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<TaslimDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AdminPolicies.Usage, policy =>
        policy.RequireAuthenticatedUser().AddRequirements(new AdminUsageRequirement()));
});
builder.Services.AddScoped<IAuthorizationHandler, AdminUsageAuthorizationHandler>();
builder.Services.AddRateLimiter(RateLimiting.Configure);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "taslim.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = isProduction ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = isProduction ? SameSiteMode.None : SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = async context =>
    {
        var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userId, out var parsedUserId))
        {
            context.RejectPrincipal();
            return;
        }

        var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(parsedUserId.ToString());
        if (user is null || !user.IsActive)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    };
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "taslim.csrf";
    options.Cookie.HttpOnly = false;
    options.Cookie.SecurePolicy = isProduction ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = isProduction ? SameSiteMode.None : SameSiteMode.Lax;
});

var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3000"];
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy =>
    policy.WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
builder.Services.AddScoped<WorkspaceAccessService>();
builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection("Billing"));
builder.Services.AddScoped<IBillingProvisioningService, BillingProvisioningService>();
builder.Services.AddScoped<IBillingAccountService, BillingAccountService>();
builder.Services.AddScoped<ICreditLedgerService, CreditLedgerService>();
builder.Services.AddScoped<ICheckoutSessionService, CheckoutSessionService>();
builder.Services.AddScoped<IPaymentLifecycleService, PaymentLifecycleService>();
builder.Services.AddScoped<IPaymentWebhookService, PaymentWebhookService>();
builder.Services.AddScoped<IPaymentReconciliationService, PaymentReconciliationService>();
builder.Services.AddSingleton<IPaymentProvider, UnconfiguredPaymentProvider>();
builder.Services.AddScoped<IAssetService, AssetService>();
builder.Services.AddScoped<IGeneratedAssetPublisher, GeneratedAssetPublisher>();
builder.Services.AddScoped<IUsageLedgerService, UsageLedgerService>();
builder.Services.AddSingleton<IUsageChargingService, SafeUsageChargingService>();
builder.Services.Configure<UsageControlOptions>(builder.Configuration.GetSection("UsageControls"));
builder.Services.AddScoped<IUsageCostControl, UsageCostControl>();
builder.Services.AddScoped<IAdminUsageService, AdminUsageService>();
builder.Services.AddScoped<IAdminOperationsService, AdminOperationsService>();
builder.Services.AddScoped<ProviderHealthService>();
builder.Services.Configure<GenerationJobOptions>(builder.Configuration.GetSection("GenerationJobs"));
builder.Services.AddScoped<IGenerationJobQueue, DatabaseGenerationJobQueue>();
builder.Services.AddScoped<IGenerationJobUsageService, GenerationJobUsageService>();
builder.Services.AddScoped<IGenerationJobService, GenerationJobService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<INotificationService>(services => services.GetRequiredService<NotificationService>());
builder.Services.AddScoped<INotificationEventWriter>(services => services.GetRequiredService<NotificationService>());
builder.Services.AddScoped<IMovieStudioService, MovieStudioService>();
builder.Services.Configure<MovieVideoOptions>(builder.Configuration.GetSection("MovieVideo"));
builder.Services.AddScoped<MovieVideoExecutionStore>();
builder.Services.AddSingleton<IMovieVideoProvider, UnavailableMovieVideoProvider>();
builder.Services.AddScoped<IActivityCenterService, ActivityCenterService>();
builder.Services.AddSingleton<IGenerationJobHandler, SystemTestGenerationJobHandler>();
if (builder.Configuration.GetValue("GenerationJobs:WorkerEnabled", !builder.Environment.IsEnvironment("Testing")))
{
    builder.Services.AddHostedService<GenerationJobWorker>();
}
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
builder.Services.Configure<ImageGenerationOptions>(builder.Configuration.GetSection("ImageGeneration"));
builder.Services.Configure<DocumentGenerationOptions>(builder.Configuration.GetSection("DocumentGeneration"));
builder.Services.Configure<PresentationGenerationOptions>(builder.Configuration.GetSection("PresentationGeneration"));
builder.Services.Configure<ResearchGenerationOptions>(builder.Configuration.GetSection("ResearchGeneration"));
builder.Services.Configure<SocialGenerationOptions>(builder.Configuration.GetSection("SocialGeneration"));
builder.Services.Configure<MusicGenerationOptions>(builder.Configuration.GetSection("MusicGeneration"));
builder.Services.Configure<VoiceGenerationOptions>(builder.Configuration.GetSection("VoiceGeneration"));
builder.Services.AddSingleton<AiModelCatalog>();
builder.Services.AddSingleton<IAiCostCalculator, AiCostCalculator>();
builder.Services.AddSingleton<AiContextBuilder>();
builder.Services.AddHttpClient<OpenAiProvider>();
builder.Services.AddHttpClient<OpenAiImageGenerationProvider>();
builder.Services.AddHttpClient<OpenAiVoiceGenerationProvider>();
builder.Services.AddHttpClient<OpenAiResearchSearchProvider>();
builder.Services.AddHttpClient<OpenAiVoiceGenerationProvider>();
builder.Services.AddSingleton<IAiModelRouter, AiModelRouter>();
builder.Services.AddSingleton<IAiProvider, MockAiProvider>();
builder.Services.AddSingleton<IAiProvider>(services => services.GetRequiredService<OpenAiProvider>());
builder.Services.AddScoped<IChatCompletionService, ChatCompletionService>();
builder.Services.AddSingleton<IImagePromptBuilder, TaslimImagePromptBuilder>();
builder.Services.AddSingleton<IImageGenerationProvider>(services => services.GetRequiredService<OpenAiImageGenerationProvider>());
builder.Services.AddSingleton<IGenerationJobHandler, ImageGenerationJobHandler>();
builder.Services.AddScoped<IGenerationJobHandler, MovieVideoGenerationJobHandler>();
builder.Services.AddSingleton<IDocumentPromptBuilder, DocumentPromptBuilder>();
builder.Services.AddScoped<IDocumentGenerationProvider, AiDocumentGenerationProvider>();
builder.Services.AddSingleton<IDocumentRenderer, DocumentRenderer>();
builder.Services.AddScoped<IGenerationJobHandler, DocumentGenerationJobHandler>();
builder.Services.AddSingleton<IPresentationPromptBuilder, PresentationPromptBuilder>();
builder.Services.AddScoped<IPresentationGenerationProvider, AiPresentationGenerationProvider>();
builder.Services.AddScoped<IPresentationRenderer, PresentationRenderer>();
builder.Services.AddScoped<IGenerationJobHandler, PresentationGenerationJobHandler>();
builder.Services.AddSingleton<IResearchPlanner, DeterministicResearchPlanner>();
builder.Services.AddSingleton<IResearchContentFetcher, PassthroughResearchContentFetcher>();
builder.Services.AddSingleton<IResearchEvidenceProcessor, DeterministicResearchEvidenceProcessor>();
builder.Services.AddSingleton<IResearchPromptBuilder, ResearchPromptBuilder>();
builder.Services.AddScoped<IResearchSearchProvider>(services => services.GetRequiredService<OpenAiResearchSearchProvider>());
builder.Services.AddScoped<IResearchReportProvider, AiResearchReportProvider>();
builder.Services.AddScoped<IGenerationJobHandler, ResearchGenerationJobHandler>();
builder.Services.AddSingleton<ISocialPromptBuilder, SocialPromptBuilder>();
builder.Services.AddScoped<ISocialGenerationProvider, AiSocialGenerationProvider>();
builder.Services.AddScoped<IGenerationJobHandler, SocialGenerationJobHandler>();
builder.Services.AddHttpClient<MubertMusicGenerationProvider>();
builder.Services.AddSingleton<IMusicGenerationProvider>(services => services.GetRequiredService<MubertMusicGenerationProvider>());
builder.Services.AddHttpClient<StableAudioMusicGenerationProvider>();
builder.Services.AddSingleton<IMusicGenerationProvider>(services => services.GetRequiredService<StableAudioMusicGenerationProvider>());
builder.Services.AddScoped<IGenerationJobHandler, MusicGenerationJobHandler>();
builder.Services.AddSingleton<IVoiceGenerationProvider>(services => services.GetRequiredService<OpenAiVoiceGenerationProvider>());
builder.Services.AddSingleton<IVoiceGenerationProvider, UnconfiguredVoiceGenerationProvider>();
builder.Services.AddSingleton<IVoiceGenerationProvider>(services => services.GetRequiredService<OpenAiVoiceGenerationProvider>());
builder.Services.AddScoped<IGenerationJobHandler, VoiceGenerationJobHandler>();
builder.Services.AddScoped<FileValidationService>();
builder.Services.AddSingleton<IFileContentExtractor, FileContentExtractor>();
builder.Services.AddScoped<FileProcessingService>();
builder.Services.AddSingleton<IFileStorageService>(services =>
{
    var settings = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileSettings>>().Value;
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileSettings>>();
    if (string.Equals(settings.StorageProvider, FileStorageProviders.Local, StringComparison.OrdinalIgnoreCase))
    {
        return new LocalFileStorageService(
            options,
            services.GetRequiredService<ILogger<LocalFileStorageService>>());
    }

    if (string.Equals(settings.StorageProvider, FileStorageProviders.S3Compatible, StringComparison.OrdinalIgnoreCase)
        && settings.IsS3Configured)
    {
        return new S3CompatibleFileStorageService(
            options,
            services.GetRequiredService<ILogger<S3CompatibleFileStorageService>>());
    }

    return new UnconfiguredFileStorageService(options);
});
var app = builder.Build();
ProductionConfigurationValidator.Validate(app.Configuration, app.Environment);

// Must run before exception handling, CORS, authentication, and antiforgery
// so Request.IsHttps reflects Railway's external HTTPS request.
app.UseForwardedHeaders();
app.UseMiddleware<RequestCorrelationMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Taslim.UnexpectedApiException");
        var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        logger.LogError(exception, "Unexpected API exception. RequestId={RequestId}; Method={Method}; Path={Path}; ExceptionType={ExceptionType}",
            context.TraceIdentifier, context.Request.Method, context.Request.Path, exception?.GetType().Name);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = new { code = "INTERNAL_ERROR", message = "An unexpected error occurred." } });
    }));
}

app.UseMiddleware<SecurityHeadersMiddleware>();
if (isProduction) app.UseHsts();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<AntiforgeryValidationMiddleware>();
app.UseAuthorization();
app.MapGet("/health", (OperationalHealthService health, HttpContext context) => Results.Ok(health.Live(context.TraceIdentifier)))
    .WithName("Health")
    .WithTags("System");
app.MapGet("/health/live", (OperationalHealthService health, HttpContext context) => Results.Ok(health.Live(context.TraceIdentifier)))
    .WithName("HealthLive")
    .WithTags("System");
static async Task<IResult> ReadinessEndpoint(OperationalHealthService health, HttpContext context, CancellationToken cancellationToken)
{
    var result = await health.ReadinessAsync(context.TraceIdentifier, cancellationToken);
    return Results.Json(result.Response, statusCode: result.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}
app.MapGet("/health/ready", ReadinessEndpoint)
    .WithName("HealthReady")
    .WithTags("System");
app.MapGet("/readiness", ReadinessEndpoint)
    .WithName("Readiness")
    .WithTags("System");
app.MapControllers();

if (builder.Configuration.GetValue("Database:ApplyMigrations", isProduction))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Taslim.DatabaseMigration");
    await DatabaseMigrator.ApplyAsync(db, logger);
    await using var roleScope = app.Services.CreateAsyncScope();
    var roleLogger = roleScope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Taslim.AdminBootstrap");
    await AdminRoleBootstrapper.EnsureConfiguredAdministratorsAsync(roleScope.ServiceProvider, builder.Configuration, roleLogger);
}

app.Run();

public partial class Program;
