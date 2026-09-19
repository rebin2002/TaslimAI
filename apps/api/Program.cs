using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Ai;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;

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
builder.Services.AddScoped<IUsageLedgerService, UsageLedgerService>();
builder.Services.AddSingleton<IUsageChargingService, SafeUsageChargingService>();
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
builder.Services.AddSingleton<AiModelCatalog>();
builder.Services.AddSingleton<IAiCostCalculator, AiCostCalculator>();
builder.Services.AddSingleton<AiContextBuilder>();
builder.Services.AddHttpClient<OpenAiProvider>();
builder.Services.AddSingleton<IAiModelRouter, AiModelRouter>();
builder.Services.AddSingleton<IAiProvider, MockAiProvider>();
builder.Services.AddSingleton<IAiProvider>(services => services.GetRequiredService<OpenAiProvider>());
builder.Services.AddScoped<IChatCompletionService, ChatCompletionService>();
var app = builder.Build();

// Must run before exception handling, CORS, authentication, and antiforgery
// so Request.IsHttps reflects Railway's external HTTPS request.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = new { code = "INTERNAL_ERROR", message = "An unexpected error occurred." } });
    }));
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseMiddleware<AntiforgeryValidationMiddleware>();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "Taslim API" }))
    .WithName("Health")
    .WithTags("System");
app.MapControllers();

if (builder.Configuration.GetValue("Database:ApplyMigrations", isProduction))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Taslim.DatabaseMigration");
    await DatabaseMigrator.ApplyAsync(db, logger);
}

app.Run();

public partial class Program;
