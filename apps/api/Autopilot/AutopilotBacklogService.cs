using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

/// <summary>Administrator request to create or update a pre-approved backlog item.</summary>
public sealed class AutopilotBacklogItemRequest
{
    public string? ItemKey { get; set; }
    public string? Title { get; set; }
    public string? AcceptanceSummary { get; set; }
    public string? Kind { get; set; }
    public bool Approved { get; set; }
    public int Priority { get; set; }
    public string? Reason { get; set; }
}

public sealed record AutopilotBacklogMutationResult(bool Succeeded, string? Reason, AutopilotBacklogItemDto? Item);

/// <summary>
/// Backlog administration. Approving work is always a human decision, so this
/// service only ever records what an authenticated administrator explicitly
/// submitted; the controller itself can never approve work, only consume it.
/// </summary>
public sealed class AutopilotBacklogService(TaslimDbContext db, TimeProvider timeProvider)
{
    public async Task<AutopilotBacklogMutationResult> UpsertAsync(
        AutopilotBacklogItemRequest request,
        Guid? actorUserId,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        var itemKey = (request.ItemKey ?? string.Empty).Trim();
        var title = (request.Title ?? string.Empty).Trim();
        if (itemKey.Length == 0 || title.Length == 0)
            return new AutopilotBacklogMutationResult(false, "item_key_and_title_required", null);
        if (string.IsNullOrWhiteSpace(request.Reason))
            return new AutopilotBacklogMutationResult(false, "reason_required", null);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var kind = AutopilotBacklogItemKinds.Normalize(request.Kind);

        var item = await db.AutopilotBacklogItems
            .FirstOrDefaultAsync(candidate => candidate.ItemKey == itemKey, cancellationToken);

        if (item is null)
        {
            item = new AutopilotBacklogItem
            {
                Id = Guid.NewGuid(),
                ItemKey = Bounded(itemKey, 120),
                CreatedAt = now,
            };
            db.AutopilotBacklogItems.Add(item);
        }

        item.Title = Bounded(title, 300);
        item.AcceptanceSummary = BoundedOptional(request.AcceptanceSummary, 2000);
        item.Kind = kind;
        item.Priority = Math.Clamp(request.Priority, 0, 10_000);
        item.UpdatedAt = now;

        // Only ordinary development work may be approved for autonomous selection.
        var approved = request.Approved && string.Equals(kind, AutopilotBacklogItemKinds.Development, StringComparison.OrdinalIgnoreCase);
        if (approved && !item.Approved)
        {
            item.Approved = true;
            item.ApprovedBy = actorUserId?.ToString();
            item.ApprovedAt = now;
            item.ApprovalNote = BoundedOptional(request.Reason, 400);
        }
        else if (!approved)
        {
            item.Approved = false;
            item.ApprovedBy = null;
            item.ApprovedAt = null;
            item.ApprovalNote = BoundedOptional(request.Reason, 400);
        }

        db.AutopilotAuditEvents.Add(AutopilotAuditFactory.Create(
            AutopilotAuditActions.BacklogItemChanged,
            AutopilotAuditOutcomes.Recorded,
            reason: Bounded(request.Reason, 400),
            statusDetail: $"approved={item.Approved};kind={item.Kind};priority={item.Priority}",
            taskId: item.ItemKey,
            actorUserId: actorUserId,
            targetId: item.Id,
            requestId: requestId,
            dryRun: false));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new AutopilotBacklogMutationResult(false, "backlog_item_conflict", null);
        }

        return new AutopilotBacklogMutationResult(true, null, Map(item));
    }

    public static AutopilotBacklogItemDto Map(AutopilotBacklogItem item) => new(
        item.Id, item.ItemKey, item.Title, item.Kind, item.Approved, item.ApprovedBy, item.ApprovedAt,
        item.Priority, item.ConsumedByWaveKey, item.ConsumedAt, item.UpdatedAt);

    private static string Bounded(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string? BoundedOptional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
