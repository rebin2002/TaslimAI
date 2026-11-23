using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;

namespace Taslim.Api.Email;

/// <summary>
/// Identity token options used for password recovery only. A distinct options
/// type is what keeps the recovery window from becoming the lifetime of every
/// other Identity token: email confirmation, phone change and two-factor tokens
/// keep the framework default because they resolve the framework provider with
/// the framework options, not these.
/// </summary>
public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    /// <summary>
    /// Provider key registered in <see cref="TokenOptions.ProviderMap"/> and
    /// selected by <see cref="TokenOptions.PasswordResetTokenProvider"/>. It is
    /// also the DataProtection purpose chain, so a password-reset token is not
    /// interchangeable with a token minted for another purpose.
    /// </summary>
    public const string ProviderName = "Taslim.PasswordResetTokenProvider";

    public PasswordResetTokenProviderOptions()
    {
        Name = ProviderName;
        TokenLifespan = TimeSpan.FromMinutes(PasswordRecoveryOptions.PasswordResetTokenLifetimeMinutes);
    }
}

/// <summary>
/// Password-reset token provider. The payload format and validation rules are the
/// framework's; only the options instance - lifetime and protection purpose - is
/// scoped to recovery.
/// </summary>
public sealed class PasswordResetTokenProvider<TUser> : IUserTwoFactorTokenProvider<TUser>
    where TUser : class
{
    private readonly DataProtectorTokenProvider<TUser> provider;

    public PasswordResetTokenProvider(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<PasswordResetTokenProviderOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        provider = new DataProtectorTokenProvider<TUser>(
            dataProtectionProvider,
            new OptionsWrapper<DataProtectionTokenProviderOptions>(options.Value),
            loggerFactory.CreateLogger<DataProtectorTokenProvider<TUser>>());
    }

    public Task<string> GenerateAsync(string purpose, UserManager<TUser> manager, TUser user) =>
        provider.GenerateAsync(purpose, manager, user);

    public Task<bool> ValidateAsync(string purpose, string token, UserManager<TUser> manager, TUser user) =>
        provider.ValidateAsync(purpose, token, manager, user);

    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<TUser> manager, TUser user) =>
        provider.CanGenerateTwoFactorTokenAsync(manager, user);
}

public static class PasswordRecoveryTokenProviderExtensions
{
    /// <summary>
    /// Routes Identity's password-reset purpose through the scoped provider and
    /// leaves every other purpose on the framework's default provider.
    /// </summary>
    public static IdentityBuilder AddPasswordResetTokenProvider(this IdentityBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<PasswordResetTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromMinutes(PasswordRecoveryOptions.PasswordResetTokenLifetimeMinutes));
        builder.Services.Configure<IdentityOptions>(options =>
            options.Tokens.PasswordResetTokenProvider = PasswordResetTokenProviderOptions.ProviderName);

        return builder.AddTokenProvider<PasswordResetTokenProvider<ApplicationUser>>(
            PasswordResetTokenProviderOptions.ProviderName);
    }
}
