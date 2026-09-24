using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Billing;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    TaslimDbContext db,
    IAntiforgery antiforgery,
    IOptions<IdentityOptions> identityOptions,
    IBillingProvisioningService billingProvisioning,
    ILogger<AuthController> logger) : ControllerBase
{
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult Csrf()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
        logger.LogInformation("CSRF token issued. TraceId={TraceId}; CookieIssued={CookieIssued}; Authenticated={Authenticated}", HttpContext.TraceIdentifier, !string.IsNullOrWhiteSpace(tokens.CookieToken), User.Identity?.IsAuthenticated == true);
        return Ok(new { token = tokens.RequestToken });
    }

    [HttpGet("password-policy")]
    [AllowAnonymous]
    public IActionResult PasswordPolicy()
    {
        var password = identityOptions.Value.Password;
        return Ok(new PasswordPolicyDto(
            password.RequiredLength,
            password.RequireUppercase,
            password.RequireLowercase,
            password.RequireDigit,
            password.RequireNonAlphanumeric,
            password.RequiredUniqueChars));
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || !IsSupportedLanguage(request.PreferredLanguage))
            return ApiResults.Validation(this, "Please provide a valid name, email, password, and language.");

        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Email.Trim(),
            Email = request.Email.Trim(),
            DisplayName = request.DisplayName.Trim(),
            PreferredLanguage = string.IsNullOrWhiteSpace(request.PreferredLanguage) ? LanguageCodes.English : request.PreferredLanguage!.ToLowerInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
            IsActive = true,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var identityResult = await userManager.CreateAsync(user, request.Password);
        if (!identityResult.Succeeded)
        {
            var passwordErrors = ToPasswordFieldErrors(identityResult.Errors);
            if (passwordErrors.Length > 0)
            {
                var fields = new Dictionary<string, string[]> { ["password"] = passwordErrors };
                return ApiResults.Validation(this, "Please choose a password that meets the requirements.", fields);
            }

            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "REGISTRATION_FAILED", "We could not create your account. Check your details and try again.");
        }

        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = $"{user.DisplayName}'s Workspace",
            Slug = Slugify(user.DisplayName, Guid.NewGuid()),
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = WorkspaceRole.Owner,
            JoinedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        await billingProvisioning.EnsureProvisionedAsync(workspace.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await signInManager.SignInAsync(user, isPersistent: true);
        return Ok(new AuthResponse(ToUserDto(user, workspace.Id), ToWorkspaceDto(workspace, WorkspaceRole.Owner)));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            logger.LogInformation("Login validation rejected request. TraceId={TraceId}; Reason=ModelValidation", HttpContext.TraceIdentifier);
            return ApiResults.Validation(this, "Please enter your email and password.");
        }

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive)
        {
            logger.LogInformation("Login validation rejected credentials. TraceId={TraceId}; Reason=InvalidCredentials", HttpContext.TraceIdentifier);
            return ApiResults.Error(this, StatusCodes.Status401Unauthorized, "INVALID_CREDENTIALS", "Invalid email or password.");
        }

        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            logger.LogInformation("Login validation rejected credentials. TraceId={TraceId}; Reason=InvalidCredentials", HttpContext.TraceIdentifier);
            return ApiResults.Error(this, StatusCodes.Status401Unauthorized, "INVALID_CREDENTIALS", "Invalid email or password.");
        }

        user.LastLoginAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        await userManager.UpdateAsync(user);
        await signInManager.SignInAsync(user, isPersistent: true);
        var workspace = await FindPersonalWorkspace(user.Id, cancellationToken);
        if (workspace is null)
            return ApiResults.Error(this, StatusCodes.Status500InternalServerError, "ACCOUNT_SETUP_INCOMPLETE", "Your account setup is incomplete. Please contact support.");

        logger.LogInformation("Login validation succeeded. TraceId={TraceId}", HttpContext.TraceIdentifier);
        return Ok(new AuthResponse(ToUserDto(user, workspace.Id), ToWorkspaceDto(workspace, WorkspaceRole.Owner)));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null || !user.IsActive)
            return Unauthorized();
        var workspace = await FindPersonalWorkspace(user.Id, cancellationToken);
        if (workspace is null)
            return ApiResults.Error(this, StatusCodes.Status500InternalServerError, "ACCOUNT_SETUP_INCOMPLETE", "Your account setup is incomplete. Please contact support.");
        return Ok(new AuthResponse(ToUserDto(user, workspace.Id), ToWorkspaceDto(workspace, WorkspaceRole.Owner)));
    }

    [HttpPost("logout")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return Ok(new { success = true });
    }

    [HttpPatch("profile")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateProfile(UpdateProfileRequest request)
    {
        if (!ModelState.IsValid || !IsSupportedLanguage(request.PreferredLanguage))
            return ApiResults.Validation(this, "Please provide a valid display name and language.");
        var user = await userManager.GetUserAsync(User);
        if (user is null || !user.IsActive) return Unauthorized();
        user.DisplayName = request.DisplayName.Trim();
        user.PreferredLanguage = request.PreferredLanguage.ToLowerInvariant();
        user.UpdatedAt = DateTime.UtcNow;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, "PROFILE_UPDATE_FAILED", "We could not update your profile.");
        var workspace = await FindPersonalWorkspace(user.Id, HttpContext.RequestAborted);
        if (workspace is null) return ApiResults.Error(this, 500, "ACCOUNT_SETUP_INCOMPLETE", "Your account setup is incomplete. Please contact support.");
        return Ok(new AuthResponse(ToUserDto(user, workspace.Id), ToWorkspaceDto(workspace, WorkspaceRole.Owner)));
    }

    private async Task<Workspace?> FindPersonalWorkspace(Guid userId, CancellationToken cancellationToken) =>
        await db.Workspaces.AsNoTracking()
            .Where(workspace => workspace.Type == WorkspaceType.Personal && workspace.Members.Any(member => member.UserId == userId && member.Role == WorkspaceRole.Owner))
            .OrderBy(workspace => workspace.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private static bool IsSupportedLanguage(string? language) => string.IsNullOrWhiteSpace(language) || LanguageCodes.Supported.Contains(language);
    private static string[] ToPasswordFieldErrors(IEnumerable<IdentityError> errors) => errors
        .Select(error => error.Code)
        .Where(code => code is "PasswordTooShort" or "PasswordRequiresUpper" or "PasswordRequiresLower" or "PasswordRequiresDigit" or "PasswordRequiresNonAlphanumeric" or "PasswordRequiresUniqueChars")
        .Select(code => code switch
        {
            "PasswordTooShort" => "PASSWORD_TOO_SHORT",
            "PasswordRequiresUpper" => "PASSWORD_REQUIRES_UPPERCASE",
            "PasswordRequiresLower" => "PASSWORD_REQUIRES_LOWERCASE",
            "PasswordRequiresDigit" => "PASSWORD_REQUIRES_DIGIT",
            "PasswordRequiresNonAlphanumeric" => "PASSWORD_REQUIRES_NON_ALPHANUMERIC",
            "PasswordRequiresUniqueChars" => "PASSWORD_REQUIRES_UNIQUE_CHARS",
            _ => "PASSWORD_REQUIREMENTS"
        })
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    private static string Slugify(string name, Guid suffix) => $"{new string(name.Trim().ToLowerInvariant().Where(character => char.IsLetterOrDigit(character) || character == ' ').ToArray()).Replace(' ', '-')}-{suffix.ToString("N")[..8]}";
    private static UserDto ToUserDto(ApplicationUser user, Guid workspaceId) => new(user.Id, user.Email ?? string.Empty, user.DisplayName, user.PreferredLanguage, workspaceId, user.CreatedAt);
    private static WorkspaceSummaryDto ToWorkspaceDto(Workspace workspace, WorkspaceRole role) => new(workspace.Id, workspace.Name, workspace.Slug, workspace.Type.ToString(), role.ToString());
}
