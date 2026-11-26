using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Email;

public sealed class PasswordResetEmailWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<PasswordRecoveryOptions> recoveryOptions,
    IEmailSender emailSender,
    IWebHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<PasswordResetEmailWorker> logger) : BackgroundService
{
    private const int BatchSize = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = recoveryOptions.CurrentValue;
                if (options.IsReady(environment.IsProduction(), emailSender))
                    await ProcessBatchAsync(options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Exception messages can contain SMTP recipient or message data.
                // Record only a stable type classification; never log a token, URL,
                // email address, provider response, or exception message.
                logger.LogError("Password reset outbox processing failed. ErrorType={ErrorType}", exception.GetType().Name);
            }

            var pollSeconds = Math.Clamp(recoveryOptions.CurrentValue.WorkerPollSeconds, 1, 60);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProcessBatchAsync(PasswordRecoveryOptions options, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var userIds = await db.PasswordResetEmailOutbox
            .AsNoTracking()
            .Where(item =>
                (item.Status == PasswordResetDeliveryStates.Pending && item.NextAttemptAt <= now)
                || (item.Status == PasswordResetDeliveryStates.Processing && item.LeaseExpiresAt <= now))
            .OrderBy(item => item.NextAttemptAt)
            .Select(item => item.UserId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var userId in userIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessEntryAsync(db, userManager, options, userId, cancellationToken);
        }
    }

    private async Task ProcessEntryAsync(
        TaslimDbContext db,
        UserManager<ApplicationUser> userManager,
        PasswordRecoveryOptions options,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entry = await db.PasswordResetEmailOutbox.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (entry is null) return;

        var pendingAndDue = entry.Status == PasswordResetDeliveryStates.Pending && entry.NextAttemptAt <= now;
        var processingLeaseExpired = entry.Status == PasswordResetDeliveryStates.Processing
            && (entry.LeaseExpiresAt is null || entry.LeaseExpiresAt <= now);
        if (!pendingAndDue && !processingLeaseExpired)
            return;

        if (entry.AttemptCount >= options.MaxDeliveryAttempts)
        {
            entry.Status = PasswordResetDeliveryStates.Failed;
            entry.LeaseExpiresAt = null;
            entry.LastFailureCode ??= "MAX_ATTEMPTS_EXCEEDED";
            await SaveIfCurrentAsync(db, entry, cancellationToken);
            return;
        }

        entry.Status = PasswordResetDeliveryStates.Processing;
        entry.LeaseExpiresAt = now.AddSeconds(options.LeaseSeconds);
        entry.AttemptCount++;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(entry).State = EntityState.Detached;
            return;
        }

        try
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null || !user.IsActive || string.IsNullOrWhiteSpace(user.Email))
            {
                entry.Status = PasswordResetDeliveryStates.Cancelled;
                entry.LeaseExpiresAt = null;
                entry.LastFailureCode = null;
                await SaveIfCurrentAsync(db, entry, cancellationToken);
                return;
            }

            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var resetUrl = PasswordResetLinkBuilder.Build(options.PublicBaseUrl, user.Email, token, user.PreferredLanguage);
            var email = PasswordResetEmailTemplate.Create(user.PreferredLanguage, resetUrl);
            await emailSender.SendAsync(user.Email, email.Subject, email.Body, cancellationToken);

            entry.Status = PasswordResetDeliveryStates.Delivered;
            entry.LeaseExpiresAt = null;
            entry.LastFailureCode = null;
            await SaveIfCurrentAsync(db, entry, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var attemptNow = timeProvider.GetUtcNow().UtcDateTime;
            var failureCode = exception is SmtpException or TimeoutException ? "SMTP_DELIVERY_FAILED" : "RESET_EMAIL_DELIVERY_FAILED";
            entry.LastFailureCode = failureCode;
            entry.LeaseExpiresAt = null;
            if (entry.AttemptCount >= options.MaxDeliveryAttempts)
            {
                entry.Status = PasswordResetDeliveryStates.Failed;
                entry.NextAttemptAt = attemptNow;
            }
            else
            {
                entry.Status = PasswordResetDeliveryStates.Pending;
                var delayMinutes = Math.Min(15, 1 << Math.Min(entry.AttemptCount, 4));
                entry.NextAttemptAt = attemptNow.AddMinutes(delayMinutes);
            }
            await SaveIfCurrentAsync(db, entry, cancellationToken);
            logger.LogWarning(
                "Password reset email delivery attempt failed. FailureCode={FailureCode}; Attempt={Attempt}",
                failureCode,
                entry.AttemptCount);
        }
    }

    private static async Task SaveIfCurrentAsync(
        TaslimDbContext db,
        PasswordResetEmailOutboxEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A newer user request may have replaced this outbox state while the
            // SMTP call was in flight. Leave the newer request intact.
            db.Entry(entry).State = EntityState.Detached;
        }
    }
}
